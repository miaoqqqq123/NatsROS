using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using NatsROS.Messages.AEM;
using System.Collections.Concurrent;
using System.Text.Json;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "中央报警管理器 (AEM)", Category = "系统核心 (System Core)", Description = "加载报警字典，防抖去重，维护全网报警生命周期")]
    public class AlarmManagerNode(INatsClient nats, string nodeName, ILogger<AlarmManagerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 报警字典：Code -> Definition
        private readonly Dictionary<string, AlarmDefinition> _alarmDict = new();

        // 活动报警池：Code -> Active State
        private readonly ConcurrentDictionary<string, ActiveAlarmState> _activeAlarms = new();

        private string _dictFilePath = "";

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 从工作区的 Config 目录下要！
            _dictFilePath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath("alarms.json"); LoadDictionary();
            return Task.CompletedTask;
        }

        /// <summary>
        /// 代码配置双向自动同步
        /// </summary>
        private void LoadDictionary()
        {
            //清空内存中的旧字典
            _alarmDict.Clear();
            bool isDirty = false;

            // 1. 如果有旧的 JSON（可能是实施人员修改过的），优先加载它！
            if (File.Exists(_dictFilePath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<AlarmDefinition>>(File.ReadAllText(_dictFilePath));
                    if (loaded != null)
                    {
                        foreach (var def in loaded) _alarmDict[def.Code] = def;
                    }
                }
                catch (Exception ex) { Logger.LogError(ex, "解析 alarms.json 失败，将使用代码默认值！"); }
            }

            // 2. 扫描 AlarmKeys 类里的所有常量，把程序员新加的报警注入进来
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                string asmName = asm.GetName().Name ?? "";

                // 【防线一】：命名规范过滤 (只放行 .Messages 结尾的业务库和核心框架库)
                if (!asmName.EndsWith(".Messages") && asmName != "NatsROS.Core" && asmName != "NatsROS.Messages")
                {
                    continue;
                }

                // 【防线二】：程序集标签过滤 (只进挂了报警牌子的 DLL)
                if (!asm.IsDefined(typeof(ContainsNatsRosAlarmsAttribute), false))
                {
                    continue;
                }

                // 安全获取类型 (应对可能的反射异常)
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }

                // 遍历合法的类型，提取带有 [AlarmDefault] 的常量
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

            // 3. 如果发现了新报警，自动重写 alarms.json 文件（对人类友好的格式）
            if (isDirty || !File.Exists(_dictFilePath))
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
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

            // 3. 监听 HMI 操作员的“确认 (ACK)”
            var ackServer = CreateServer<AckAlarmReq, AckAlarmRes>("aem.ack");
            _ = ackServer.ServeAsync(req =>
            {
                bool ok = HandleAck(req.Code);
                return Task.FromResult(new AckAlarmRes(ok));
            }, stoppingToken);

            // 4. 监听 HMI 开机同步请求
            var syncServer = CreateServer<SyncAlarmsReq, SyncAlarmsRes>("aem.sync");
            _ = syncServer.ServeAsync(req =>
            {
                return Task.FromResult(new SyncAlarmsRes(_activeAlarms.Values.ToList()));
            }, stoppingToken);


            // 5.监听工程热切换广播，瞬间重载配置！
            var workspaceSub = CreateSubscriber<WorkspaceChangedEvent>("sys.workspace.changed");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in workspaceSub.SubscribeAsync(stoppingToken))
                {
                    Logger.LogWarning("🔄 收到工程 [{Project}] 切换广播！正在清空并热重载 AEM 报警字典...", msg.NewProjectName);
                    LoadDictionary();
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // ==========================================
        // 核心状态机逻辑
        // ==========================================

        private void HandleRaise(RaiseAlarmMsg msg)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _activeAlarms.AddOrUpdate(msg.Code,
                // Add: 如果是新报警，去字典查翻译，生成新状态
                code =>
                {
                    if (!_alarmDict.TryGetValue(code, out var def))
                        def = new AlarmDefinition(code, AlarmLevel.Warning, "未知报警代码: {0}", false, false); // 兜底

                    string formattedMsg = def.Template;
                    if (msg.Args != null && msg.Args.Length > 0)
                    {
                        try { formattedMsg = string.Format(def.Template, msg.Args); } catch { }
                    }

                    Logger.LogWarning("🔴 新增活动报警: [{Code}] {Msg}", code, formattedMsg);
                    return new ActiveAlarmState(code, def.Level, formattedMsg, AlarmStatus.Raised, now, now, 1, def.IsLatching);
                },
                // Update: 【防风暴！】如果报警已经在池子里了，绝对不新建，只更新发生次数和时间！
                (code, existing) =>
                {
                    Logger.LogDebug("🔁 抑制报警风暴: [{Code}] 发生次数+1", code);
                    // 使用 record 的 with 魔法进行无损更新
                    return existing with
                    {
                        LastRaisedTime = now,
                        Occurrences = existing.Occurrences + 1,
                        Status = AlarmStatus.Raised // 如果之前被消除了但没出池子，重新激活
                    };
                }
            );

            BroadcastStateChange();
        }

        private void HandleClear(ClearAlarmMsg msg)
        {
            if (_activeAlarms.TryGetValue(msg.Code, out var state))
            {
                // 如果是非自锁的，且底层报了 Clear，直接从池子里删掉！
                if (!state.IsLatching)
                {
                    _activeAlarms.TryRemove(msg.Code, out _);
                    Logger.LogInformation("🟢 非自锁报警物理恢复，自动消除: [{Code}]", msg.Code);
                }
                else
                {
                    // 如果是自锁的，只能变成 Cleared 状态，等待工人 Ack 才能删掉
                    _activeAlarms[msg.Code] = state with { Status = AlarmStatus.Cleared };
                    Logger.LogInformation("🟡 自锁报警物理恢复，等待人工复位: [{Code}]", msg.Code);
                }
                BroadcastStateChange();
            }
        }

        private bool HandleAck(string code)
        {
            if (_activeAlarms.TryGetValue(code, out var state))
            {
                // 如果这个报警已经被底层物理排除了 (Cleared)，工人一 Ack，它就彻底消失！
                if (state.Status == AlarmStatus.Cleared)
                {
                    _activeAlarms.TryRemove(code, out _);
                    Logger.LogInformation("🟢 报警已彻底复位: [{Code}]", code);
                }
                else
                {
                    // 故障还在，工人只是点了一下“消音/我知道了”
                    _activeAlarms[code] = state with { Status = AlarmStatus.Acknowledged };
                    Logger.LogInformation("🔕 报警已被人工确认(消音): [{Code}]", code);
                }
                BroadcastStateChange();
                return true;
            }
            return false;
        }

        private void BroadcastStateChange()
        {
            // 向全网 HMI 广播最新的活动报警池
            _ = Nats.PublishAsync("aem.changed", new AlarmsChangedEvent(_activeAlarms.Values.ToList()));
        }
    }
}