using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using NatsROS.Messages.Motion;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Examples
{
    [RosNode(DisplayName = "模拟单轴控制器", Category = "运动控制 (Motion)", Description = "接收运动 Action，模拟真实的物理插补与耗时移动")]
    public class SimulatedAxisNode(INatsClient nats, string nodeName, ILogger<SimulatedAxisNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private double _currentPosition = 0.0;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("⚙️ 模拟轴 [{NodeName}] 硬件初始化完成，当前位置: {Pos} mm", Name, _currentPosition);

            // 创建 Action 服务端，路由地址动态绑定为 "{节点名}.move"
            var moveServer = CreateActionServer<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult>($"{Name}.move");

            await moveServer.ServeAsync(async (goal, feedback, cancelToken) =>
            {
                Logger.LogInformation("🚀 收到移动指令！目标: {Target} mm, 速度: {Vel} mm/s", goal.TargetPosition, goal.Velocity);

                // 物理运动模拟逻辑
                double distance = Math.Abs(goal.TargetPosition - _currentPosition);
                int direction = goal.TargetPosition > _currentPosition ? 1 : -1;

                // 假设控制周期为 100 毫秒 (10Hz)
                int cycleMs = 100;
                double stepDistance = goal.Velocity * (cycleMs / 1000.0); // 每个周期的移动步长

                while (Math.Abs(goal.TargetPosition - _currentPosition) > 0.001)
                {
                    // 【关键】：检查急停信号！如果收到取消，瞬间抛出异常刹车！
                    cancelToken.ThrowIfCancellationRequested();

                    // 如果剩余距离小于一个步长，直接到达目标
                    if (Math.Abs(goal.TargetPosition - _currentPosition) <= stepDistance)
                    {
                        _currentPosition = goal.TargetPosition;
                    }
                    else
                    {
                        _currentPosition += direction * stepDistance;
                    }

                    // 【关键】：实时推送编码器位置！
                    feedback(new AxisMoveFeedback(_currentPosition));

                    await Task.Delay(cycleMs, cancelToken); // 模拟耗时
                }

                Logger.LogInformation("✅ 轴移动到位！最终位置: {Pos} mm", _currentPosition);
                return new AxisMoveResult(true, _currentPosition, "到位完成");

            }, stoppingToken);
        }
    }
}