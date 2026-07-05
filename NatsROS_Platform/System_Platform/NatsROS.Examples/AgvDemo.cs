using MessagePack;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Examples
{
    // ==========================================
    // 1. 终极实战：消息契约定义
    // ==========================================

    // Topic: 遥测数据
    [MessagePackObject]
    public record AgvTelemetry([property: Key(0)] double X, [property: Key(1)] double Y, [property: Key(2)] int Battery) : IRosMessage;

    // Service: 鸣笛指令
    [MessagePackObject]
    public record BeepReq([property: Key(0)] int DurationMs) : IRosRequest<BeepRes>;
    [MessagePackObject]
    public record BeepRes([property: Key(0)] bool Success) : IRosMessage;

    // Action: 导航动作
    [MessagePackObject]
    public record NavGoal(
        [property: Key(0)] double TargetX, 
        [property: Key(1)] double TargetY
    ) : IRosActionGoal<NavFeedback, NavResult>;
    
    [MessagePackObject]
    public record NavFeedback(
        [property: Key(0)] double DistanceRemaining
        ) : IRosMessage;
   
    [MessagePackObject]
    public record NavResult(
        [property: Key(0)] bool Success, 
        [property: Key(1)] string Message = ""
    ) : IRosMessage;

    // ==========================================
    // 2. AGV 底盘节点 (被控端)
    // ==========================================
    public class AgvChassisNode(INatsClient nats, string nodeName, ILogger<AgvChassisNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private double _currentX = 0;
        private double _currentY = 0;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🤖 AGV 底盘初始化完成，各系统上线。");
            _currentX = 0;
            _currentY = 0;
            // --- Parameter 模块 ---
            Parameters.SetLocal("max_speed", "2.0"); // 默认速度 2.0 m/s
            Parameters.OnParameterChanged += (name, val) =>
                Logger.LogWarning("⚙️ AGV 硬件接收到参数热更新：{Name} = {Val}", name, val);

            // --- Service 模块 ---
            var beepServer = CreateServer<BeepReq, BeepRes>("agv.beep");
            _ = beepServer.ServeAsync(async req =>
            {
                Logger.LogWarning("🔊 AGV 鸣笛 {Duration} 毫秒！滴滴滴——", req.DurationMs);
                await Task.Delay(req.DurationMs, stoppingToken);
                return new BeepRes(true);
            }, stoppingToken);

            // --- Action 模块 ---
            var navAction = CreateActionServer<NavGoal, NavFeedback, NavResult>("agv.navigate");
            _ = navAction.ServeAsync(async (goal, feedback, cancelToken) =>
            {
                Logger.LogInformation("🗺️ AGV 开始执行导航任务，目标: ({X}, {Y})", goal.TargetX, goal.TargetY);
                _currentX = 0;
                while (!cancelToken.IsCancellationRequested)
                {
                    double dx = goal.TargetX - _currentX;
                    double distance = dx;

                    if (distance <= 0.5) // 到达目的地
                    {
                        _currentX = goal.TargetX; _currentY = goal.TargetY;
                        Logger.LogInformation("🏁 AGV 已抵达目的地！");
                        return new NavResult(true, "Arrived");
                    }

                    // 读取动态限速参数，决定这一步走多远
                    double speed = double.Parse(Parameters.GetLocal("max_speed", "2.0"));

                    // 简单的直线插补移动
                    _currentX +=  speed;

                    // 推送进度 Feedback
                    feedback(new NavFeedback(distance));
                    Logger.LogInformation($"AGV 距离目的地还有{distance}");
                    await Task.Delay(1000, cancelToken); // 模拟 1Hz 的物理运动周期
                }
                cancelToken.ThrowIfCancellationRequested();
                return new NavResult(false, "Unknown");
            }, stoppingToken);

            // --- Topic 模块 (后台持续发送遥测数据) ---
            var telemetryPub = CreatePublisher<AgvTelemetry>("agv.telemetry");
            int battery = 100;
            while (!stoppingToken.IsCancellationRequested)
            {
                await telemetryPub.PublishAsync(new AgvTelemetry(_currentX, _currentY, battery), stoppingToken);
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    // ==========================================
    // 3. 调度中心节点 (主控端)
    // ==========================================
    public class MissionControlNode(INatsClient nats, string nodeName, ILogger<MissionControlNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(2000, stoppingToken); // 等待 AGV 完全启动
            Logger.LogInformation("📡 调度中心开始执行 AGV 编排剧本...");

            // 1. 调用 Service: 让 AGV 鸣笛警告
            Logger.LogInformation("📡 [动作1] 发送鸣笛指令...");
            var beepClient = CreateClient<BeepReq, BeepRes>("agv.beep");
            await beepClient.CallAsync(new BeepReq(1000), TimeSpan.FromSeconds(3), stoppingToken);

            // 2. 调用 Action: 下达导航任务 (目标坐标: 20, 0)
            Logger.LogInformation("📡 [动作2] 下达导航任务...");
            var navClient = CreateActionClient<NavGoal, NavFeedback, NavResult>("agv.navigate");

            // 我们不 await 等待它结束，因为我们要在它走到一半的时候做点手脚！
            var navTask = navClient.SendGoalAsync(new NavGoal(20, 0), feedback =>
            {
                Logger.LogDebug("📡 [导航监控] 收到反馈，距离终点还有: {Dist:F2} 米", feedback.DistanceRemaining);
            }, stoppingToken);

            // 3. 调用 Parameter: 动态提速 (在导航途中)
            await Task.Delay(4000, stoppingToken); // 等它慢悠悠走 4 秒
            Logger.LogWarning("📡 [动作3] 嫌它走得太慢？调度中心发起参数热更新，限速改为 8.0 m/s！");

            var paramClient = CreateParameterClient("agv_1"); // 假设底盘节点叫 agv_1
            await paramClient.SetAsync("max_speed", "8.0", stoppingToken);

            // 4. 等待 Action 最终结果
            var result = await navTask;
            if (result?.Status == ActionStatus.Succeeded)
            {
                Logger.LogInformation("🎉 调度任务圆满结束！");
            }
        }
    }
}
