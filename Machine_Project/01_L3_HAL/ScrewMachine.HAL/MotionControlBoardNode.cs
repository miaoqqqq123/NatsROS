using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using ScrewMachine.Messages.Hardware;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Core.Communication;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "雷赛控制卡 (Motion & IO)", Category = "底层硬件 (HAL)", Description = "独占底层 SDK，统一管理伺服轴与 32入/32出 物理 IO")]
    public class MotionControlBoardNode(INatsClient nats, string nodeName, ILogger<MotionControlBoardNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 模拟底层板卡的 32 个物理输出引脚状态
        private readonly ConcurrentDictionary<int, bool> _outPins = new();
        // 模拟调用底层 C++ SDK 时的物理锁，防止多线程把板卡打崩溃
        private readonly object _sdkLock = new object();

        private RosPublisher<IoStateChangedMsg>? _ioPub;

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 初始化 32 个引脚为 false
            for (int i = 0; i < 32; i++) _outPins[i] = false;
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🛠️ 控制卡 [{NodeName}] 初始化成功，已接管底层硬件 SDK。", Name);

            _ioPub = CreatePublisher<IoStateChangedMsg>($"{Name}.io.state");

            // 监听全网对 IO 的写请求
            var setIoServer = CreateServer<SetIoReq, SetIoRes>($"{Name}.io.set");
            _ = setIoServer.ServeAsync(async req =>
            {
                if (req.Pin < 0 || req.Pin >= 32) return new SetIoRes(false, "引脚越界");

                // 【模拟底层 SDK 调用耗时与锁】
                lock (_sdkLock)
                {
                    _outPins[req.Pin] = req.State;
                    // 假设调用 C++ SDK 函数: Leisai_WriteOutBit(0, req.Pin, req.State);
                }

                // IO 状态改变，向全网广播 (供大屏的指示灯使用！)
                if (_ioPub != null) await _ioPub.PublishAsync(new IoStateChangedMsg(req.Pin, req.State));

                // 故意加一点物理通讯延迟
                await Task.Delay(10, stoppingToken);
                return new SetIoRes(true);
            }, stoppingToken);

            // 监听全网对 IO 的读请求
            var getIoServer = CreateServer<GetIoReq, GetIoRes>($"{Name}.io.get");
            _ = getIoServer.ServeAsync(req =>
            {
                if (_outPins.TryGetValue(req.Pin, out bool state)) return Task.FromResult(new GetIoRes(state, true));
                return Task.FromResult(new GetIoRes(false, false));
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}