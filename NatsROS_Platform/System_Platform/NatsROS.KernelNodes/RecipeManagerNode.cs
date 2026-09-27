using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Serialization;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using NatsROS.KernelNodes.Database;
using NatsROS.Messages.AEM;
using NatsROS.Messages.RMS;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "中央配方与审计系统 (RMS)", Category = "系统核心 (System Core)", Description = "提供符合 ISA-88 的工艺配方管理与 FDA 级别 SQLite 审计追踪")]
    public class RecipeManagerNode(INatsClient nats, string nodeName, ILogger<RecipeManagerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private readonly ConcurrentDictionary<string, RecipeModel> _recipes = new();
        private string _recipeDir = "";
        private RecipeModel? _activeRecipe;

        private readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new RawJsonDictionaryConverter() } // 集合初始化语法，极其优雅！
        };

        protected override async Task OnConfigureAsync(CancellationToken ct)
        {
            // 1. 获取配方文件夹路径
            _recipeDir = NatsROS.Core.Environment.WorkspaceManager.GetRecipeDirectoryPath();
            LoadRecipes();

            // 2. 初始化 SQLite 审计黑匣子 (EF Core)
            using var db = new RmsDbContext();
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        }

        /// <summary>
        /// 从文件夹遍历读取所有 JSON
        /// </summary>
        private void LoadRecipes()
        {
            _recipes.Clear();
            if (Directory.Exists(_recipeDir))
            {
                var files = Directory.GetFiles(_recipeDir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var r = JsonSerializer.Deserialize<RecipeModel>(File.ReadAllText(file), _jsonOpts);
                        if (r != null) _recipes[r.RecipeId] = r;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "加载配方文件失败: {File}", file);
                    }
                }
                Logger.LogInformation("✅ 成功加载工程配方库，共 {Count} 个独立配方文件。", _recipes.Count);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("📋 RMS 配方系统已激活，当前载入 {Count} 个配方。", _recipes.Count);

            var getSrv = CreateServer<GetRecipesReq, GetRecipesRes>("rms.get_recipes");
            _ = getSrv.ServeAsync(req => Task.FromResult(new GetRecipesRes(_recipes.Values.ToList())), stoppingToken);

            // 【新增】：配方激活机制与全局广播
            // 尝试从参数服务器恢复上次关机前的激活配方
            string lastActiveId = Parameters.GetLocal("ActiveRecipeId", "");
            if (!string.IsNullOrEmpty(lastActiveId) && _recipes.TryGetValue(lastActiveId, out var lastRecipe))
            {
                _activeRecipe = lastRecipe;
            }

            var activateSrv = CreateServer<ActivateRecipeReq, ActivateRecipeRes>("rms.activate");
            _ = activateSrv.ServeAsync(req =>
            {
                if (_recipes.TryGetValue(req.RecipeId, out var recipe))
                {
                    _activeRecipe = recipe;
                    // 将激活状态存入参数服务器，保证重启不丢失
                    Parameters.SetLocal("ActiveRecipeId", req.RecipeId);

                    // 【核心】：向全网广播！视觉节点、行为树节点、大屏都会收到这个消息！
                    _ = Nats.PublishAsync("rms.event.recipe_activated", new RecipeActivatedEvent(recipe, DateTime.UtcNow.Ticks));

                    Logger.LogInformation("🎯 配方 [{Id}] 已被激活为当前产线工作配方！", req.RecipeId);
                    return Task.FromResult(new ActivateRecipeRes(true, "激活成功"));
                }
                return Task.FromResult(new ActivateRecipeRes(false, "找不到指定的配方"));
            }, stoppingToken);

            var getActiveSrv = CreateServer<GetActiveRecipeReq, GetActiveRecipeRes>("rms.get_active");
            _ = getActiveSrv.ServeAsync(req => Task.FromResult(new GetActiveRecipeRes(_activeRecipe)), stoppingToken);

            var auditSrv = CreateServer<GetAuditLogsReq, GetAuditLogsRes>("rms.get_audits");
            _ = auditSrv.ServeAsync(async req =>
            {
                return new GetAuditLogsRes(await GetAuditLogsFromDbAsync(req.RecipeId));
            }, stoppingToken);

            var saveSrv = CreateServer<SaveRecipeReq, SaveRecipeRes>("rms.save");
            _ = saveSrv.ServeAsync(async req =>
            {
                var newR = req.Recipe;
                bool isNew = !_recipes.TryGetValue(newR.RecipeId, out var oldR);

                if (!isNew && oldR!.State == RecipeState.Approved)
                {
                    Logger.LogWarning("⛔ 非法操作: 试图修改已批准的配方 [{Id}]！被 RMS 系统拦截。", newR.RecipeId);
                    return new SaveRecipeRes(false, "已批准的配方严禁修改！请更改版本号另存为新配方。");
                }

                string diffDetails = "";
                if (!isNew && oldR!.PayloadJson != newR.PayloadJson)
                {
                    var diffs = new List<string>();
                    try
                    {
                        // 1. 将旧的与新的 PayloadJson 动态解析为 JsonObject
                        var oldNode = System.Text.Json.Nodes.JsonNode.Parse(oldR.PayloadJson) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
                        var newNode = System.Text.Json.Nodes.JsonNode.Parse(newR.PayloadJson) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();

                        // 2. 找出被修改或新增的参数
                        foreach (var kvp in newNode)
                        {
                            string key = kvp.Key;
                            string newValStr = kvp.Value?.ToJsonString() ?? "null";

                            if (oldNode.TryGetPropertyValue(key, out var oldValNode))
                            {
                                string oldValStr = oldValNode?.ToJsonString() ?? "null";
                                if (newValStr != oldValStr)
                                {
                                    // 【智能排版】：如果改的是巨大无比的点位或轨迹，不要把几百行的JSON全打进日志里！
                                    if (key.StartsWith("RecipePoint_") || key.StartsWith("RecipeTraj_"))
                                        diffs.Add($"[点位/轨迹] {key} 数据已更新");
                                    else
                                        diffs.Add($"[{key}] 由 {oldValStr} 变为 {newValStr}");
                                }
                            }
                            else
                            {
                                diffs.Add($"[新增] 参数 {key} = {newValStr}");
                            }
                        }

                        // 3. 找出被删除的参数
                        foreach (var kvp in oldNode)
                        {
                            if (!newNode.ContainsKey(kvp.Key))
                            {
                                diffs.Add($"[删除] 参数 {kvp.Key}");
                            }
                        }

                        if (diffs.Count > 0) diffDetails = "系统捕获变更:\r\n" + string.Join("\r\n", diffs);
                    }
                    catch (Exception ex)
                    {
                        diffDetails = "系统捕获变更: 强类型配方工艺参数已被修改 (JSON 比对失败: " + ex.Message + ")";
                    }
                }

                newR = newR with
                {
                    State = RecipeState.Draft,
                    LastModifiedBy = req.OperatorName,
                    LastModifiedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                _recipes[newR.RecipeId] = newR;

                string action = isNew ? "Create" : "Update";
                string details = isNew ? "操作: 创建了新配方" : $"操作: 修改了配方\r\n原因: {req.ChangeReason}\r\n详情: {diffDetails}";

                // 异步落盘，绝对不卡顿
                await AppendAuditLogToDbAsync(req.OperatorName, newR.RecipeId, action, details);
                SaveSingleRecipeToDisk(newR);

                // 数据落盘后，向全网广播配方已更新！
                _ = Nats.PublishAsync("rms.event.recipe_updated", new RecipeUpdatedEvent(newR.RecipeId, DateTime.UtcNow.Ticks));

                Logger.LogInformation("✅ 配方已保存: [{Id}] v{Ver}", newR.RecipeId, newR.Version);
                return new SaveRecipeRes(true, "保存成功，状态已重置为 Draft。");
            }, stoppingToken);

            var stateSrv = CreateServer<ChangeRecipeStateReq, ChangeRecipeStateRes>("rms.change_state");
            _ = stateSrv.ServeAsync(async req =>
            {
                if (!_recipes.TryGetValue(req.RecipeId, out var recipe))
                    return new ChangeRecipeStateRes(false, "配方不存在");

                _recipes[req.RecipeId] = recipe with { State = req.TargetState };

                await AppendAuditLogToDbAsync(req.OperatorName, req.RecipeId, "ChangeState", $"将状态从 {recipe.State} 变更为 {req.TargetState}");
                SaveSingleRecipeToDisk(_recipes[req.RecipeId]);

                // 状态流转后，同样向全网广播！
                _ = Nats.PublishAsync("rms.event.recipe_updated", new RecipeUpdatedEvent(req.RecipeId, DateTime.UtcNow.Ticks));

                Logger.LogInformation("🔐 配方 [{Id}] 状态已变更为: {State}", req.RecipeId, req.TargetState);
                return new ChangeRecipeStateRes(true, "状态流转成功");
            }, stoppingToken);

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
        /// 【核心修改 2】：精准覆盖单个配方文件，不再全量重写
        /// </summary>
        /// <param name="recipe"></param>
        private void SaveSingleRecipeToDisk(RecipeModel recipe)
        {
            Task.Run(() =>
            {
                // 以 RecipeId 作为文件名 (如: RECIPE_APPLE_001.json)
                string filePath = Path.Combine(_recipeDir, $"{recipe.RecipeId}.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(recipe, _jsonOpts));
            });
        }

        // ==========================================
        // 替换为 EF Core 的异步操作
        // ==========================================
        private async Task AppendAuditLogToDbAsync(string operatorName, string recipeId, string action, string details)
        {
            using var db = new RmsDbContext();
            await db.AuditLogs.AddAsync(new AuditLogEntity
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Operator = operatorName ?? "",
                RecipeId = recipeId ?? "",
                Action = action ?? "",
                Details = details ?? ""
            });

            // 对于 FDA 审计日志，通常要求强一致性，所以这里我们直接 SaveChangesAsync 不用 Channel 缓冲
            await db.SaveChangesAsync();
        }

        private async Task<List<AuditLogRecord>> GetAuditLogsFromDbAsync(string recipeId)
        {
            using var db = new RmsDbContext();
            var query = db.AuditLogs.AsNoTracking();

            if (!string.IsNullOrEmpty(recipeId)) query = query.Where(e => e.RecipeId == recipeId);

            // 限制取 1000 条防 OOM
            var entities = await query.OrderByDescending(e => e.Timestamp).Take(1000).ToListAsync();

            return entities.Select(e => new AuditLogRecord(
                e.Timestamp, e.Operator, e.RecipeId, e.Action, e.Details
            )).ToList();
        }
    }
}