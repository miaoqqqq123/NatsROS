using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using NatsROS.Messages.RMS;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "中央配方与审计系统 (RMS)", Category = "系统核心 (System Core)", Description = "提供符合 ISA-88 的工艺配方管理与 FDA 级别 SQLite 审计追踪")]
    public class RecipeManagerNode(INatsClient nats, string nodeName, ILogger<RecipeManagerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private readonly ConcurrentDictionary<string, RecipeModel> _recipes = new();

        private string _recipeDbPath = "";
        private string _auditDbPath = "";
        private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 配方字典放入工程目录的 Recipes 文件夹
            _recipeDbPath = NatsROS.Core.Environment.WorkspaceManager.GetRecipeDbPath();

            // 审计日志 (FDA 铁证) 必须锁死在本机数据区！
            _auditDbPath = NatsROS.Core.Environment.WorkspaceManager.GetLocalDatabasePath("rms_audits.db");

            // 1. 加载配方 JSON 库
            LoadRecipes();

            // 2. 初始化 SQLite 审计黑匣子
            InitializeAuditDatabase();

            return Task.CompletedTask;
        }

        /// <summary>
        /// 独立加载配方的方法
        /// </summary>
        private void LoadRecipes()
        {
            _recipes.Clear(); // 清空旧工程的配方
            if (File.Exists(_recipeDbPath))
            {
                try
                {
                    var list = JsonSerializer.Deserialize<List<RecipeModel>>(File.ReadAllText(_recipeDbPath));
                    if (list != null) foreach (var r in list) _recipes[r.RecipeId] = r;
                    Logger.LogInformation("✅ 成功加载工程配方库，共 {Count} 个配方。", _recipes.Count);
                }
                catch (Exception ex) 
                { 
                    Logger.LogError(ex, "加载配方库失败");
                }
            }
        }


        /// <summary>
        /// SQLite 数据库初始化与建表
        /// </summary>

        private void InitializeAuditDatabase()
        {
            var connStr = $"Data Source={_auditDbPath}";
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            var cmd = conn.CreateCommand();
            // 如果表不存在则创建
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS AuditLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp INTEGER NOT NULL,
                    Operator TEXT NOT NULL,
                    RecipeId TEXT NOT NULL,
                    Action TEXT NOT NULL,
                    Details TEXT NOT NULL
                )";
            cmd.ExecuteNonQuery();
            Logger.LogInformation("🗄️ SQLite 审计黑匣子已连接！");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("📋 RMS 配方系统已激活，当前载入 {Count} 个配方。", _recipes.Count);

            // 1. 获取全量配方 (HMI / Dashboard 使用)
            var getSrv = CreateServer<GetRecipesReq, GetRecipesRes>("rms.get_recipes");
            _ = getSrv.ServeAsync(req => Task.FromResult(new GetRecipesRes(_recipes.Values.ToList())), stoppingToken);

            // 2. 获取审计日志 (从 SQLite 中拉取)
            var auditSrv = CreateServer<GetAuditLogsReq, GetAuditLogsRes>("rms.get_audits");
            _ = auditSrv.ServeAsync(req =>
            {
                return Task.FromResult(new GetAuditLogsRes(GetAuditLogsFromDb(req.RecipeId)));
            }, stoppingToken);


            // 3. 极其严谨的保存/修改逻辑
            var saveSrv = CreateServer<SaveRecipeReq, SaveRecipeRes>("rms.save");
            _ = saveSrv.ServeAsync(req =>
            {
                var newR = req.Recipe;
                bool isNew = !_recipes.TryGetValue(newR.RecipeId, out var oldR);

                // 【核心合规墙】：如果旧配方已经是 Approved 状态，绝对不允许覆盖修改！只能另存为新 ID（如 V1.1）
                if (!isNew && oldR!.State == RecipeState.Approved)
                {
                    Logger.LogWarning("⛔ 非法操作: 试图修改已批准的配方 [{Id}]！被 RMS 系统拦截。", newR.RecipeId);
                    return Task.FromResult(new SaveRecipeRes(false, "已批准的配方严禁修改！请更改版本号另存为新配方。"));
                }

                // ==========================================
                // 【核心升级】：自动生成客观的参数修改 Diff 记录！
                // ==========================================
                string diffDetails = "";
                if (!isNew)
                {
                    var oldFormula = oldR!.Formula;
                    var newFormula = newR.Formula;
                    var diffs = new List<string>();

                    // 1. 查找值被修改的参数
                    foreach (var key in newFormula.Keys.Intersect(oldFormula.Keys))
                    {
                        if (newFormula[key] != oldFormula[key])
                            diffs.Add($"[{key}]由'{oldFormula[key]}'变为'{newFormula[key]}'");
                    }

                    // 2. 查找新增的参数
                    foreach (var key in newFormula.Keys.Except(oldFormula.Keys))
                    {
                        diffs.Add($"新增参数[{key}]='{newFormula[key]}'");
                    }

                    // 3. 查找被删除的参数
                    foreach (var key in oldFormula.Keys.Except(newFormula.Keys))
                    {
                        diffs.Add($"删除了参数[{key}]");
                    }

                    if (diffs.Count > 0)
                    {
                        diffDetails = "系统捕获变更: " + string.Join("; ", diffs);
                    }
                }

                // 强制修正部分属性，防止篡改
                newR = newR with
                {
                    State = RecipeState.Draft, // 修改后强制回退为草稿
                    LastModifiedBy = req.OperatorName,
                    LastModifiedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                _recipes[newR.RecipeId] = newR;

                // 审计追溯
                string action = isNew ? "Create" : "Update";
                string details = isNew 
                ? "操作: 创建了新配方" 
                : $"操作: 修改了配方\r\n" +
                $"原因: {req.ChangeReason}\r\n" +
                $"详情: {diffDetails}";
                AppendAuditLogToDb(req.OperatorName, newR.RecipeId, action, details);

                SaveDataToDisk();

                Logger.LogInformation("✅ 配方已保存: [{Id}] v{Ver}", newR.RecipeId, newR.Version);

                return Task.FromResult(new SaveRecipeRes(true, "保存成功，状态已重置为 Draft。"));
            }, stoppingToken);

            // 4. 配方状态审核流转 (审批发布)
            var stateSrv = CreateServer<ChangeRecipeStateReq, ChangeRecipeStateRes>("rms.change_state");
            _ = stateSrv.ServeAsync(req =>
            {
                if (!_recipes.TryGetValue(req.RecipeId, out var recipe))
                    return Task.FromResult(new ChangeRecipeStateRes(false, "配方不存在"));

                _recipes[req.RecipeId] = recipe with { State = req.TargetState };

                AppendAuditLogToDb(req.OperatorName, req.RecipeId, "ChangeState", $"将状态从 {recipe.State} 变更为 {req.TargetState}");
                SaveDataToDisk();

                Logger.LogInformation("🔐 配方 [{Id}] 状态已变更为: {State}", req.RecipeId, req.TargetState);
                return Task.FromResult(new ChangeRecipeStateRes(true, "状态流转成功"));
            }, stoppingToken);

            // 5.监听工程热切换
            var workspaceSub = CreateSubscriber<WorkspaceChangedEvent>("sys.workspace.changed");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in workspaceSub.SubscribeAsync(stoppingToken))
                {
                    Logger.LogWarning("🔄 收到工程 [{Project}] 切换广播！正在清空并热重载 RMS 配方库...", msg.NewProjectName);
                    LoadRecipes();
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        /// <summary>
        /// 仅配方字典异步存盘
        /// </summary>
        private void SaveDataToDisk()
        {
            Task.Run(() =>
            {
                File.WriteAllText(_recipeDbPath, JsonSerializer.Serialize(_recipes.Values.ToList(), _jsonOpts));
            });
        }


        /// <summary>
        /// SQLite 极速写操作
        /// </summary>
        /// <param name="operatorName"></param>
        /// <param name="recipeId"></param>
        /// <param name="action"></param>
        /// <param name="details"></param>


        private void AppendAuditLogToDb(string operatorName, string recipeId, string action, string details)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var conn = new SqliteConnection($"Data Source={_auditDbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO AuditLogs (Timestamp, Operator, RecipeId, Action, Details) VALUES (@ts, @op, @rid, @act, @det)";
            cmd.Parameters.AddWithValue("@ts", timestamp);
            cmd.Parameters.AddWithValue("@op", operatorName ?? "");
            cmd.Parameters.AddWithValue("@rid", recipeId ?? "");
            cmd.Parameters.AddWithValue("@act", action ?? "");
            cmd.Parameters.AddWithValue("@det", details ?? "");
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// SQLite 极速读操作
        /// </summary>
        /// <param name="recipeId"></param>
        /// <returns></returns>
        private List<AuditLogRecord> GetAuditLogsFromDb(string recipeId)
        {
            var logs = new List<AuditLogRecord>();
            using var conn = new SqliteConnection($"Data Source={_auditDbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();

            if (string.IsNullOrEmpty(recipeId))
            {
                // 全局查询：为了防止几万条记录卡死 UI，强制限制最后 1000 条
                cmd.CommandText = "SELECT Timestamp, Operator, RecipeId, Action, Details FROM AuditLogs ORDER BY Timestamp DESC LIMIT 1000";
            }
            else
            {
                // 单个配方查询
                cmd.CommandText = "SELECT Timestamp, Operator, RecipeId, Action, Details FROM AuditLogs WHERE RecipeId = @rid ORDER BY Timestamp DESC";
                cmd.Parameters.AddWithValue("@rid", recipeId);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                logs.Add(new AuditLogRecord(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)
                ));
            }
            return logs;
        }
    }
}