using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using ScrewMachine.Messages.Hardware;
using System;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Messages.Hardware;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "工业塔灯控制模块", Category = "外围外设 (Peripherals)", Description = "后台独立状态机，映射系统状态并控制底层 IO 闪烁")]
    public class TowerLightNode(INatsClient nats, string nodeName, ILogger<TowerLightNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private LightState _redState = LightState.Off;
        private LightState _yellowState = LightState.Off;
        private LightState _greenState = LightState.Off;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int redPin = int.Parse(Parameters.GetLocal("RedPin", "0"));
            int yellowPin = int.Parse(Parameters.GetLocal("YellowPin", "1"));
            int greenPin = int.Parse(Parameters.GetLocal("GreenPin", "2"));

            // 绑定底板的 IO 服务
            string boardName = Parameters.GetLocal("BoardNodeName", "motionboard_1");
            var ioClient = new RosServiceClient<SetIoReq, SetIoRes>(Nats, $"{boardName}.io.set");

            var lightServer = CreateServer<SetTowerLightReq, SetTowerLightRes>($"{Name}.set");
            _ = lightServer.ServeAsync(req =>
            {
                _redState = req.Red; _yellowState = req.Yellow; _greenState = req.Green;
                Logger.LogInformation("🚥 塔灯状态更新: 红={R}, 黄={Y}, 绿={G}", _redState, _yellowState, _greenState);
                return Task.FromResult(new SetTowerLightRes(true));
            }, stoppingToken);

            // ==========================================
            // 塔灯独立闪烁协程 (完全不阻塞业务主线程)
            // ==========================================
            _ = Task.Run(async () =>
            {
                bool toggle = false;
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // 红灯逻辑
                        bool r = _redState == LightState.Solid || (_redState == LightState.Blinking && toggle);
                        //await ioClient.CallAsync(new SetIoReq(redPin, r));

                        // 黄灯逻辑
                        bool y = _yellowState == LightState.Solid || (_yellowState == LightState.Blinking && toggle);
                        //await ioClient.CallAsync(new SetIoReq(yellowPin, y));

                        // 绿灯逻辑
                        bool g = _greenState == LightState.Solid || (_greenState == LightState.Blinking && toggle);
                        //await ioClient.CallAsync(new SetIoReq(greenPin, g));

                        toggle = !toggle;
                        await Task.Delay(500, stoppingToken); // 500ms 闪烁周期
                    }
                    catch { /* 忽略底板未上线的报错 */ }
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}