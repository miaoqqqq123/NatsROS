using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using NatsROS.Messages.Motion;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "虚拟三轴点胶机", Category = "数字孪生 (Digital Twin)", Description = "接收三维坐标并模拟空间直线插补与点胶动作")]
    public class SimulatedDispenserNode(INatsClient nats, string nodeName, ILogger<SimulatedDispenserNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private double _currentX = 0.0;
        private double _currentY = 0.0;
        private double _currentZ = 50.0;
        private bool _isValveOpen = false; // 胶阀状态

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🦾 虚拟三轴点胶机[{NodeName}] 硬件初始化完成。", Name);

            // 1. 注册阀门控制服务
            var valveServer = CreateServer<ValveControlReq, ValveControlRes>($"{Name}.valve");
            _ = valveServer.ServeAsync(req =>
            {
                _isValveOpen = req.Open;
                Logger.LogInformation("💉 胶阀状态切换为: {State}", _isValveOpen ? "🟢 开启 (ON)" : "🔴 关闭 (OFF)");
                return Task.FromResult(new ValveControlRes(true));
            }, stoppingToken);

            // 2. 注册单点移动 (PTP)
            var moveServer = CreateActionServer<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>($"{Name}.move");
            _ = moveServer.ServeAsync(async (goal, feedback, ct) =>
                await ExecuteInterpolationAsync(goal.TargetX, goal.TargetY, goal.TargetZ, goal.Velocity, feedback, ct),
                stoppingToken);

            // 3. 注册连续轨迹 (CP)
            var trajServer = CreateActionServer<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult>($"{Name}.trajectory");
            _ = trajServer.ServeAsync(async (goal, feedback, ct) =>
            {
                Logger.LogInformation("🚀 开始执行连续轨迹插补！共 {Count} 个路径点。", goal.Path.Length);
                foreach (var target in goal.Path)
                {
                    // 依次向每个点进行插补，中途不断速
                    var res = await ExecuteInterpolationAsync(target.X, target.Y, target.Z, goal.Velocity, feedback, ct);
                    if (!res.IsSuccess) return res;
                }
                Logger.LogInformation("✅ 连续轨迹执行完毕！");
                return new DispenserMoveResult(true, "轨迹完成");
            }, stoppingToken);
        }

        // 核心空间直线插补算法
        private async Task<DispenserMoveResult> ExecuteInterpolationAsync(double targetX, double targetY, double targetZ, double velocity, Action<DispenserMoveFeedback> feedback, CancellationToken ct)
        {
            int cycleMs = 20; // 50Hz 高频插补，使得 3D 轨迹极其丝滑
            while (!ct.IsCancellationRequested)
            {
                double dx = targetX - _currentX; double dy = targetY - _currentY; double dz = targetZ - _currentZ;
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (distance <= 0.001)
                {
                    _currentX = targetX; _currentY = targetY; _currentZ = targetZ;
                    feedback(new DispenserMoveFeedback(_currentX, _currentY, _currentZ, _isValveOpen));
                    break;
                }

                double stepDistance = velocity * (cycleMs / 1000.0);
                if (stepDistance > distance) stepDistance = distance;

                double ratio = stepDistance / distance;
                _currentX += dx * ratio; _currentY += dy * ratio; _currentZ += dz * ratio;

                feedback(new DispenserMoveFeedback(_currentX, _currentY, _currentZ, _isValveOpen));
                await Task.Delay(cycleMs, ct);
            }
            ct.ThrowIfCancellationRequested();
            return new DispenserMoveResult(true, "到位");
        }
    }
}