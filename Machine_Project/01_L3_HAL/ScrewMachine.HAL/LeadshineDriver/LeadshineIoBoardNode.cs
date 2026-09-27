using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.Hardware;

namespace ScrewMachine.HAL.LeadshineDriver
{
    [RosNode(DisplayName = "雷赛IO板卡驱动", Category = "L3 硬件驱动", Description = "统一管理 EtherCAT 总线 IO 点的读写与语义化标签映射")]
    public class LeadshineIoBoardNode : HostedRosNode
    {
        [RosProp(DisplayName = "IO点刷新频率(ms)", DefaultValue = "100")]
        public int PollingRateMs { get; set; } = 100;

        // 【核心魔法】：变成强类型的内存字典！
        private Dictionary<string, IoPointDefinition> _ioMap = new();
        private string _configFilePath = "";

        public LeadshineIoBoardNode(INatsClient nats, string nodeName, ILogger<LeadshineIoBoardNode> logger)
            : base(nats, nodeName, logger) { }

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            LeadshineBusManager.RequestOpen(Logger);

            // 1. 节点自己去沙盒里读自己专属的强类型配置文件！
            // 不再依赖弱类型的 Parameter Server
            _configFilePath = NatsROS.Core.Environment.WorkspaceManager.GetConfigPath($"{Name}_io_definitions.json");

            if (File.Exists(_configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_configFilePath);
                    var list = JsonSerializer.Deserialize<List<IoPointDefinition>>(json);
                    if (list != null) _ioMap = list.ToDictionary(p => p.TagName);

                    Logger.LogInformation("✅ IO 标签映射解析成功，共加载 {Count} 个语义标签。", _ioMap.Count);
                }
                catch (Exception ex) { Logger.LogError(ex, "❌ IO 字典 JSON 解析失败！"); }
            }
            else
            {
                // 默认种子数据
                _ioMap["DI_CYL_A_WORK"] = new IoPointDefinition("DI_CYL_A_WORK", 0, IoType.Input, "气缸A工作位传感器");
                _ioMap["DO_CYL_A_PUSH"] = new IoPointDefinition("DO_CYL_A_PUSH", 2, IoType.Output, "气缸A顶出电磁阀");
            }

            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🔌 雷赛IO板卡节点 [{NodeName}] 已启动，正在监听网络指令...", Name);

            // ==========================================
            // RPC 1: 获取字典 (供大屏查询)
            // ==========================================
            CreateServer<GetIoDictReq, GetIoDictRes>($"{Name}.io.get_dict").ServeAsync(req =>
            {
                return Task.FromResult(new GetIoDictRes(true, _ioMap.Values.ToList()));
            }, stoppingToken);

            // ==========================================
            // RPC 2: 保存字典 (供大屏配置并热更新)
            // ==========================================
            CreateServer<SaveIoDictReq, SaveIoDictRes>($"{Name}.io.save_dict").ServeAsync(req =>
            {
                try
                {
                    _ioMap = req.IoPoints.ToDictionary(p => p.TagName);

                    // 异步落盘，保护现场
                    var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(_configFilePath, JsonSerializer.Serialize(req.IoPoints, opts));

                    Logger.LogInformation("🔄 IO 字典已通过 RPC 热重载！");
                    return Task.FromResult(new SaveIoDictRes(true, "保存并下发成功"));
                }
                catch (Exception ex) { return Task.FromResult(new SaveIoDictRes(false, ex.Message)); }
            }, stoppingToken);

            // ==========================================
            // RPC 3: 响应读写状态
            // ==========================================
            CreateServer<SetIoReq, SetIoRes>($"{Name}.io.set").ServeAsync(req =>
            {
                if (!_ioMap.TryGetValue(req.TagName, out var point) || point.Type != IoType.Output)
                    return Task.FromResult(new SetIoRes(false, $"标签[{req.TagName}]无效或非输出点"));

                // TODO: 替换为实际的 LeadshineBusManager.GetContext().IoStates[point.PhysicalPin] = req.State; 
                return Task.FromResult(new SetIoRes(true));
            }, stoppingToken);

            CreateServer<GetIoReq, GetIoRes>($"{Name}.io.get").ServeAsync(req =>
            {
                if (!_ioMap.TryGetValue(req.TagName, out var point))
                    return Task.FromResult(new GetIoRes(false, false));

                // TODO: 读取物理状态
                bool state = false; // LeadshineBusManager.GetContext().IoStates.TryGetValue(point.PhysicalPin, out var s) && s;
                return Task.FromResult(new GetIoRes(state, true));
            }, stoppingToken);

            // ==========================================
            // 循环广播：只广播字典里定义了的标签
            // ==========================================
            var statePub = CreatePublisher<IoStateChangedMsg>($"{Name}.io.state", RosQosProfile.SensorData);
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var point in _ioMap.Values)
                    {
                        // TODO: bool state = LeadshineBusManager.GetContext().IoStates.TryGetValue(point.PhysicalPin, out var s) && s;
                        await statePub.PublishAsync(new IoStateChangedMsg(point.TagName, point.PhysicalPin, false), stoppingToken);
                    }
                    await Task.Delay(PollingRateMs, stoppingToken);
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        protected override Task OnCleanupAsync(CancellationToken ct)
        {
            LeadshineBusManager.RequestClose();
            return Task.CompletedTask;
        }
    }
}