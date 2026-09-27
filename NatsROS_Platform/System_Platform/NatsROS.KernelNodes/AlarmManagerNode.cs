using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using NatsROS.KernelNodes.Database;
using NatsROS.Messages.AEM;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "中央报警管理器 (AEM)", Category = "系统核心 (System Core)", Description = "加载报警字典，防抖去重，维护全网报警生命周期与历史追溯")]
    public class AlarmManagerNode(INatsClient nats, string nodeName, ILogger<AlarmManagerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 报警字典：Code -> Definition
        private readonly Dictionary<string, AlarmDefinition> _alarmDict = new();

        // 活动报警池：Code -> Active State
        private readonly ConcurrentDictionary<string, ActiveAlarmState> _activeAlarms = new();

        // 屏蔽白名单：Code -> 过期时间 (Bypass/Shelving)
        private readonly ConcurrentDictionary<string, DateTime> _bypassedAlarms = new();

        private string _dictFilePath = "";

        protected override async Task OnConfigureAsync(CancellationToken ct)
        {
            // 1. 从工作区的 Config 目录下要字典配置！
            _dictFilePath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("alarms.json");
            LoadDictionary();

            // 2. 初始化 SQLite 报警历史库 (FDA Part 11)
            using var db = new AemDbContext();
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        }

        /// <summary>
        /// 代码与 JSON 字典双向自动同步
        /// </summary>
        private void LoadDictionary()
        {
            _alarmDict.Clear();
            bool isDirty = false;

            // 1. 如果有旧的 JSON（可能是实施人员修改过的），优先加载它
            if (File.Exists(_dictFilePath))
            {
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = true
                    };

                    var loaded = JsonSerializer.Deserialize<List<AlarmDefinition>>(File.ReadAllText(_dictFilePath), options);
                    if (loaded != null)
                    {
                        foreach (var def in loaded) _alarmDict[def.Code] = def;
                    }
                }
                catch (Exception ex)
                { 
                    Logger.LogError(ex, "解析 alarms.json 失败，将使用代码默认值！"); 
                }
            }

            // 2. 扫描加载进内存的 DLL，把程序员新加的报警注入进来
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                string asmName = asm.GetName().Name ?? "";

                // 命名规范过滤 & 标签过滤
                if (!asmName.EndsWith(".Messages") && asmName != "NatsROS.Core" && asmName != "NatsROS.Messages")
                    continue;

                if (!asm.IsDefined(typeof(ContainsNatsRosAlarmsAttribute), false))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                foreach (var type in types)
                {
                    var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    foreach (var field in fields)
                    {
                        if (field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
                        {
                            string code = (string)field.GetRawConstantValue()!;
                            if (!_alarmDict.ContainsKey(code))
                            {
                                var attr = field.GetCustomAttributes(typeof(AlarmDefaultAttribute), false).FirstOrDefault() as AlarmDefaultAttribute;
                                if (attr != null)
                                {
                                    _alarmDict[code] = new AlarmDefinition(code, attr.Level, attr.Template, attr.IsLatching, attr.AllowBypass);
                                    isDirty = true;
                                    Logger.LogInformation("✨ 发现新报警定义 [{Code}] 来自 {Asm}，已自动注入系统字典！", code, asmName);
                                }
                            }
                        }
                    }
                }
            }

            // 3. 自动更新 alarms.json 
            if (isDirty || !File.Exists(_dictFilePath))
            {
                var options = new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true 
                };
                File.WriteAllText(_dictFilePath, JsonSerializer.Serialize(_alarmDict.Values.ToList(), options));
                Logger.LogWarning("📝 alarms.json 已自动更新并同步了最新的代码契约！");
            }

            Logger.LogInformation("✅ 成功加载报警字典，共 {Count} 条定义。", _alarmDict.Count);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🚨 报警管理器已激活，正在全网监控异常...");

            // 1. 监听底层节点抛出异常 (防风暴算法)
            var raiseSub = CreateSubscriber<RaiseAlarmMsg>("aem.raise");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in raiseSub.SubscribeAsync(stoppingToken))
                {
                    if (msg != null) HandleRaise(msg);
                }
            }, stoppingToken);

            // 2. 监听底层节点物理消除异常
            var clearSub = CreateSubscriber<ClearAlarmMsg>("aem.clear");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in clearSub.SubscribeAsync(stoppingToken))
                {
                    if (msg != null) HandleClear(msg);
                }
            }, stoppingToken);

            // 3. RPC: 监听确认 (ACK)
            var ackServer = CreateServer<AckAlarmReq, AckAlarmRes>("aem.ack");
            _ = ackServer.ServeAsync(req =>
            {
                bool ok = HandleAck(req.Code, req.OperatorName);
                return Task.FromResult(new AckAlarmRes(ok));
            }, stoppingToken);

            // 4. RPC: 监听 HMI 开机同步请求
            var syncServer = CreateServer<SyncAlarmsReq, SyncAlarmsRes>("aem.sync");
            _ = syncServer.ServeAsync(req =>
            {
                return Task.FromResult(new SyncAlarmsRes(_activeAlarms.Values.ToList()));
            }, stoppingToken);

            // 5. 【新增 RPC】: 历史记录查询
            var historySrv = CreateServer<GetAlarmHistoryReq, GetAlarmHistoryRes>("aem.history");
            _ = historySrv.ServeAsync(async req =>
            {
                using var db = new AemDbContext();
                var entities = await db.AlarmHistories.AsNoTracking().OrderByDescending(e => e.Timestamp).Take(req.Limit).ToListAsync(stoppingToken);
                var records = entities.Select(e => new AlarmHistoryRecord(e.Timestamp, e.Code, e.Action, e.Operator, e.Details)).ToList();
                return new GetAlarmHistoryRes(records);
            }, stoppingToken);

            // 6. 【新增 RPC】: 报警屏蔽/搁置 (Bypass)
            var bypassSrv = CreateServer<BypassAlarmReq, BypassAlarmRes>("aem.bypass");
            _ = bypassSrv.ServeAsync(req =>
            {
                if (!_alarmDict.TryGetValue(req.Code, out var def) || !def.AllowBypass)
                {
                    Logger.LogWarning("⛔ 屏蔽请求被拒：报警 [{Code}] 在字典中配置为不运行 Bypass！", req.Code);
                    return Task.FromResult(new BypassAlarmRes(false, "该报警配置为不允许屏蔽！"));
                }

                // 加入屏蔽字典
                _bypassedAlarms[req.Code] = DateTime.Now.AddMinutes(req.DurationMinutes);

                // 从活动报警池中强制移除
                _activeAlarms.TryRemove(req.Code, out _);
                BroadcastStateChange();

                // 记入历史
                _ = AppendHistoryAsync(req.Code, "Bypassed", req.Operator, $"人工屏蔽 {req.DurationMinutes} 分钟");
                Logger.LogInformation("🔕 报警 [{Code}] 已被 {Op} 成功屏蔽 {Min} 分钟", req.Code, req.Operator, req.DurationMinutes);

                return Task.FromResult(new BypassAlarmRes(true, $"报警已成功屏蔽 {req.DurationMinutes} 分钟"));
            }, stoppingToken);

            // 7. 监听工程热切换广播
            var workspaceSub = CreateSubscriber<WorkspaceChangedEvent>("sys.workspace.changed");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in workspaceSub.SubscribeAsync(stoppingToken))
                {
                    Logger.LogWarning("🔄 收到工程 [{Project}] 切换广播！正在清空并热重载 AEM 报警字典...", msg.NewProjectName);
                    _activeAlarms.Clear();
                    _bypassedAlarms.Clear();
                    LoadDictionary();
                    BroadcastStateChange();
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // ==========================================
        // 核心状态机逻辑
        // ==========================================

        private void HandleRaise(RaiseAlarmMsg msg)
        {
            // 【核心拦截】：如果该报警处于被屏蔽期内，直接装瞎！
            if (_bypassedAlarms.TryGetValue(msg.Code, out var expiryTime))
            {
                if (DateTime.Now < expiryTime) return;
                _bypassedAlarms.TryRemove(msg.Code, out _); // 屏蔽过期，清理掉
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool isNewRaise = false; // 用于判断是否要写历史记录

            _activeAlarms.AddOrUpdate(msg.Code,
                code =>
                {
                    if (!_alarmDict.TryGetValue(code, out var def))
                        def = new AlarmDefinition(code, AlarmLevel.Warning, "未知报警代码: {0}", false, false);

                    string formattedMsg = def.Template;
                    if (msg.Args != null && msg.Args.Length > 0)
                    {
                        try { formattedMsg = string.Format(def.Template, msg.Args); } catch { }
                    }

                    Logger.LogWarning("🔴 新增活动报警: [{Code}] {Msg}", code, formattedMsg);
                    isNewRaise = true;
                    // 注意：这里需要传入 9 个参数，对应我们刚在 AlarmModels.cs 里修改的 ActiveAlarmState！
                    return new ActiveAlarmState(code, def.Level, formattedMsg, AlarmStatus.Raised, now, now, 1, def.IsLatching, def.AllowBypass);
                },
                (code, existing) =>
                {
                    if (existing.Status == AlarmStatus.Cleared) isNewRaise = true; // 复发
                    Logger.LogDebug("🔁 抑制报警风暴: [{Code}] 发生次数+1", code);
                    return existing with
                    {
                        LastRaisedTime = now,
                        Occurrences = existing.Occurrences + 1,
                        Status = AlarmStatus.Raised
                    };
                }
            );

            if (isNewRaise) _ = AppendHistoryAsync(msg.Code, "Raised", "System", "设备底层触发报警");
            BroadcastStateChange();
        }

        private void HandleClear(ClearAlarmMsg msg)
        {
            if (_activeAlarms.TryGetValue(msg.Code, out var state))
            {
                if (!state.IsLatching)
                {
                    // 1. 非自锁报警，物理恢复直接消失
                    _activeAlarms.TryRemove(msg.Code, out _);
                    Logger.LogInformation("🟢 非自锁报警物理恢复，自动消除: [{Code}]", msg.Code);
                    _ = AppendHistoryAsync(msg.Code, "Cleared", "System", "非自锁报警物理恢复，已自动移除");
                }
                else
                {
                    if (state.Status == AlarmStatus.Acknowledged)
                    {
                        // 2. 如果自锁报警【已经被工人确认过(Acked)】，现在物理又恢复了，说明生命周期结束，彻底消除！
                        _activeAlarms.TryRemove(msg.Code, out _);
                        Logger.LogInformation("🟢 自锁报警物理恢复，且已被人工确认，彻底消除: [{Code}]", msg.Code);
                        _ = AppendHistoryAsync(msg.Code, "Cleared & Removed", "System", "物理恢复且已确认，从活动池彻底移除");
                    }
                    else
                    {
                        // 3. 如果还没被确认过，那就变成黄色，等待人工确认
                        _activeAlarms[msg.Code] = state with { Status = AlarmStatus.Cleared };
                        Logger.LogInformation("🟡 自锁报警物理恢复，等待人工复位: [{Code}]", msg.Code);
                        _ = AppendHistoryAsync(msg.Code, "Cleared", "System", "自锁报警物理恢复，等待人工确认");
                    }
                }
                BroadcastStateChange();
            }
        }

        /// <summary>
        /// 处理人工确认 (ACK) 请求
        /// </summary>
        /// <param name="code"></param>
        /// <param name="operatorName"></param>
        /// <returns></returns>
        private bool HandleAck(string code, string operatorName)
        {
            if (_activeAlarms.TryGetValue(code, out var state))
            {
                // 如果前端没传名字，才用兜底名字
                string op = string.IsNullOrEmpty(operatorName) ? "System" : operatorName;

                if (state.Status == AlarmStatus.Cleared)
                {
                    _activeAlarms.TryRemove(code, out _);
                    Logger.LogInformation("🟢 报警已彻底复位: [{Code}] by {Op}", code, op);
                    _ = AppendHistoryAsync(code, "Acked & Removed", op, "已确认并从活动池彻底移除");
                }
                else
                {
                    _activeAlarms[code] = state with { Status = AlarmStatus.Acknowledged };
                    Logger.LogInformation("🔕 报警已被人工确认(消音): [{Code}] by {Op}", code, op);
                    _ = AppendHistoryAsync(code, "Acknowledged", op, "操作员已知悉，但物理故障未排除");
                }
                BroadcastStateChange();
                return true;
            }
            return false;
        }

        private void BroadcastStateChange()
        {
            _ = Nats.PublishAsync("aem.changed", new AlarmsChangedEvent(_activeAlarms.Values.ToList()));
        }

        // ==========================================
        // 高频异步轻量化写库 (历史追溯)
        // ==========================================
        private async Task AppendHistoryAsync(string code, string action, string op, string details)
        {
            try
            {
                using var db = new AemDbContext();
                await db.AlarmHistories.AddAsync(new AlarmHistoryEntity
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Code = code,
                    Action = action,
                    Operator = op,
                    Details = details
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "❌ 报警历史记录写入 SQLite 失败: {Code}", code);
            }
        }
    }
}