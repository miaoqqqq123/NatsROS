using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using NatsROS.Messages.AEM;
using NatsROS.Messages.SensorMsgs;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "虚拟视觉引导系统", Category = "数字孪生 (Digital Twin)", Description = "模拟相机拍照与 Mark 点匹配计算，带异常抛出能力")]
    public class SimulatedVisionNode(INatsClient nats, string nodeName, ILogger<SimulatedVisionNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("👁️ 虚拟相机 [{NodeName}] 初始化完成，准备就绪。", Name);

            var visionServer = CreateServer<FindMarkReq, FindMarkRes>($"{Name}.find_mark");

            // 【新增】：创建报警的发布者 (抛出 和 消除)
            var raiseAlarmPub = CreatePublisher<RaiseAlarmMsg>("aem.raise");
            var clearAlarmPub = CreatePublisher<ClearAlarmMsg>("aem.clear");

            await visionServer.ServeAsync(async req =>
            {
                Logger.LogInformation("📸 收到拍照请求！正在采集图像...");
                await Task.Delay(500, stoppingToken); // 模拟耗时

                // 【核心逻辑】：30% 概率触发视觉丢失故障！
                if (Random.Shared.NextDouble() < 0.8)
                {
                    Logger.LogWarning("⚠️ [模拟视觉异常] 光线反光，无法提取 Mark 点轮廓！");

                    // 享受完美的代码提示，再也不会拼错报警码！
                    await raiseAlarmPub.PublishAsync(new RaiseAlarmMsg(AlarmKeys.VSN_MARK_NOT_FOUND, new[] { Name }), stoppingToken);

                    return new FindMarkRes(false, 0, 0, 0);
                }

                // 【核心逻辑】：一旦成功找到了，立刻发信号给 AEM：“我这边的物理故障排除了！”
                await clearAlarmPub.PublishAsync(new ClearAlarmMsg("ERR_VSN_001"), stoppingToken);

                double ox = double.Parse(Parameters.GetLocal("TruthOffsetX", "0"));
                double oy = double.Parse(Parameters.GetLocal("TruthOffsetY", "0"));
                double ang = double.Parse(Parameters.GetLocal("TruthAngle", "0"));

                Logger.LogInformation("✅ 视觉运算成功。偏差 X:{OffsetX}mm, Y:{OffsetY}mm, 旋转:{Angle}°", ox, oy, ang);
                return new FindMarkRes(true, ox, oy, ang);

            }, stoppingToken);
        }
    }
}