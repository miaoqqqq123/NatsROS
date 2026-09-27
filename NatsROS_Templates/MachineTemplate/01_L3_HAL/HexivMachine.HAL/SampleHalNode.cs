using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using HexivMachine.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace HexivMachine.HAL
{
    [RosNode(DisplayName = "示例硬件驱动", Category = "L3 示例", Description = "这是一个模板生成的示例节点")]
    public class SampleHalNode(INatsClient nats, string nodeName, ILogger<SampleHalNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("✅ 示例硬件节点 [{NodeName}] 已启动！", Name);

            var server = CreateServer<SampleEchoReq, SampleEchoRes>($"{Name}.echo");
            _ = server.ServeAsync(req =>
            {
                Logger.LogInformation("收到大屏指令: {Text}", req.Text);
                return Task.FromResult(new SampleEchoRes($"底层的回音: {req.Text}"));
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}