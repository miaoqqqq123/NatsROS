using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.Motion;
using NatsROS.Messages.Hardware;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "精密点胶控制器", Category = "外围外设 (Peripherals)", Description = "处理点胶开关的物理延时，底层驱动电磁阀 IO")]
    public class DispenserValveNode(INatsClient nats, string nodeName, ILogger<DispenserValveNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int valvePin = int.Parse(Parameters.GetLocal("ValveIoPin", "5"));
            int openDelayMs = int.Parse(Parameters.GetLocal("OpenDelayMs", "50"));
            int closeDelayMs = int.Parse(Parameters.GetLocal("CloseDelayMs", "100"));
            string boardName = Parameters.GetLocal("BoardNodeName", "motionboard_1");

            var ioClient = new RosServiceClient<SetIoReq, SetIoRes>(Nats, $"{boardName}.io.set");

            var valveServer = CreateServer<ValveControlReq, ValveControlRes>($"{Name}.valve");
            _ = valveServer.ServeAsync(async req =>
            {
                try
                {
                    if (req.Open)
                    {
                        Logger.LogInformation("💧 下发开胶信号 (Pin={Pin})... 等待出胶延迟 {Ms}ms", valvePin, openDelayMs);
                        await ioClient.CallAsync(new SetIoReq(valvePin, true));
                        await Task.Delay(openDelayMs, stoppingToken); // 气缸建立气压的物理延迟
                    }
                    else
                    {
                        Logger.LogInformation("🛑 下发断胶信号 (Pin={Pin})... 等待回吸延迟 {Ms}ms", valvePin, closeDelayMs);
                        await ioClient.CallAsync(new SetIoReq(valvePin, false));
                        await Task.Delay(closeDelayMs, stoppingToken); // 胶水回吸防止拉丝的延迟
                    }
                    return new ValveControlRes(true);
                }
                catch (Exception ex)
                {
                    Logger.LogError("点胶阀操作失败，可能是底板脱机: {Msg}", ex.Message);
                    return new ValveControlRes(false);
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}