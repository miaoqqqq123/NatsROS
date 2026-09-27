using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.Motion;
// 引入刚才写的通用契约

namespace ScrewMachine.HAL // 换成你实际的命名空间
{
    [RosNode(DisplayName = "虚拟伺服轴仿真器", Category = "L3 数字孪生", Description = "纯软件模拟的伺服电机，支持使能、急停、PTP插补与高频状态广播")]
    public class SimulatedAxisNode(INatsClient nats, string nodeName, ILogger<SimulatedAxisNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 模拟硬件寄存器
        private double _actualPosition = 0.0;
        private double _actualVelocity = 0.0;
        private bool _isServoOn = false;
        private bool _isMoving = false;
        private bool _isAlarm = false;

        // 用于处理急停指令切断正在进行的动作
        private CancellationTokenSource _hardwareCts = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("⚙️ 虚拟轴 [{NodeName}] 硬件初始化完成，正在建立控制接口...", Name);

            // ==========================================
            // 1. 开启高频状态泵 (10Hz 广播当前状态，供 3D 孪生大屏使用)
            // ==========================================
            var statePub = CreatePublisher<AxisStateMsg>($"{Name}.state", RosQosProfile.SensorData);
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await statePub.PublishAsync(new AxisStateMsg(
                        ActualPosition: Math.Round(_actualPosition, 3),
                        ActualVelocity: Math.Round(_actualVelocity, 3),
                        IsServoOn: _isServoOn,
                        IsMoving: _isMoving,
                        IsAlarm: _isAlarm,
                        LimitPositive: _actualPosition >= 1000.0, // 假设物理极限是 1000
                        LimitNegative: _actualPosition <= -1000.0,
                        ErrorCode: _isAlarm ? 99 : 0
                    ), stoppingToken);

                    await Task.Delay(100, stoppingToken);
                }
            }, stoppingToken);

            // ==========================================
            // 2. 注册 RPC 服务 (使能、复位、急停)
            // ==========================================
            CreateServer<AxisEnableReq, AxisEnableRes>($"{Name}.enable").ServeAsync(req =>
            {
                _isServoOn = req.Enable;
                Logger.LogInformation("🔌 轴 [{NodeName}] 使能状态 -> {State}", Name, _isServoOn ? "ON" : "OFF");
                return Task.FromResult(new AxisEnableRes(true));
            }, stoppingToken);

            CreateServer<AxisResetReq, AxisResetRes>($"{Name}.reset").ServeAsync(req =>
            {
                _isAlarm = false;
                Logger.LogInformation("🔄 轴 [{NodeName}] 硬件报警已复位", Name);
                return Task.FromResult(new AxisResetRes(true));
            }, stoppingToken);

            CreateServer<AxisStopReq, AxisStopRes>($"{Name}.stop").ServeAsync(req =>
            {
                // 瞬间切断所有正在运行的运动 Task
                _hardwareCts.Cancel();
                _hardwareCts = new CancellationTokenSource(); // 重置供下次使用
                Logger.LogWarning("🛑 轴 [{NodeName}] 收到停止指令！模式: {Mode}", Name, req.Mode);
                return Task.FromResult(new AxisStopRes(true));
            }, stoppingToken);

            // ==========================================
            // 3. 注册 Action 动作 (移动 PTP)
            // ==========================================
            var moveServer = CreateActionServer<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult>($"{Name}.move");
            _ = moveServer.ServeAsync(async (goal, feedback, actionCt) =>
            {
                if (!_isServoOn) return new AxisMoveResult(false, _actualPosition, "伺服未使能");
                if (_isAlarm) return new AxisMoveResult(false, _actualPosition, "伺服存在报警");

                // 将网络取消信号与硬件急停信号合并
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(actionCt, _hardwareCts.Token);

                double targetPos = goal.Mode == MoveMode.Absolute ? goal.TargetPosition : _actualPosition + goal.TargetPosition;
                Logger.LogInformation("🚀 轴 [{NodeName}] 开始移动 -> 目标: {Target} mm, 速度: {Vel}", Name, targetPos, goal.Velocity);

                _isMoving = true;
                _actualVelocity = goal.Velocity;

                try
                {
                    // 模拟极度丝滑的 50Hz 物理插补
                    int cycleMs = 20;
                    double step = goal.Velocity * (cycleMs / 1000.0);

                    while (Math.Abs(targetPos - _actualPosition) > 0.001)
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();

                        if (Math.Abs(targetPos - _actualPosition) <= step)
                            _actualPosition = targetPos;
                        else
                            _actualPosition += (targetPos > _actualPosition ? 1 : -1) * step;

                        // 软限位防撞保护
                        if (_actualPosition >= 1000 || _actualPosition <= -1000)
                        {
                            _isAlarm = true;
                            throw new Exception("触碰物理软限位！");
                        }

                        // 实时向网络汇报进度
                        feedback(new AxisMoveFeedback(_actualPosition));
                        await Task.Delay(cycleMs, linkedCts.Token);
                    }

                    Logger.LogInformation("✅ 轴 [{NodeName}] 移动到位", Name);
                    return new AxisMoveResult(true, _actualPosition, "到达目标");
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("⚠️ 轴 [{NodeName}] 移动被中途打断！当前位置: {Pos}", Name, _actualPosition);
                    return new AxisMoveResult(false, _actualPosition, "被强行打断");
                }
                catch (Exception ex)
                {
                    Logger.LogError("❌ 轴 [{NodeName}] 运动异常: {Msg}", Name, ex.Message);
                    return new AxisMoveResult(false, _actualPosition, ex.Message);
                }
                finally
                {
                    _isMoving = false;
                    _actualVelocity = 0;
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}