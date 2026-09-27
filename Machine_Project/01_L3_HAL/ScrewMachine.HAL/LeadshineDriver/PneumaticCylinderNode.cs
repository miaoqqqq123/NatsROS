using MessagePack;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.AEM; // 引入报警契约
using NatsROS.Messages; // 引入 IO 通信契约
using ScrewMachine.Messages.Hardware;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Messages.Hardware;

namespace Laser7Machine.HAL.LeadshineDriver
{
    // ==========================================
    // 气缸运动目标 (Action Goal) 
    // (注：正规流程应放于 Messages 项目，为方便测试暂放于此)
    // ==========================================
    public enum CylinderState : byte { ResetPos = 0, WorkPos = 1 }

    [MessagePackObject]
    public record CylinderMoveGoal(
        [property: Key(0)] CylinderState TargetState,
        [property: Key(1)] int TimeoutMs = 5000 // 气缸运动超时时间
    ) : IRosActionGoal<CylinderMoveFeedback, CylinderMoveResult>;

    [MessagePackObject]
    public record CylinderMoveFeedback([property: Key(0)] CylinderState CurrentState, [property: Key(1)] string Message) : IRosMessage;

    [MessagePackObject]
    public record CylinderMoveResult([property: Key(0)] bool IsSuccess, [property: Key(1)] string Message) : IRosMessage;


    // ==========================================
    // 气缸微服务节点 (PneumaticCylinderNode)
    // ==========================================
    [RosNode(DisplayName = "气缸组合节点", Category = "L3 硬件驱动", Description = "封装气缸的运动逻辑，完全依赖语义化 IO 标签进行跨节点通信")]
    public class PneumaticCylinderNode : HostedRosNode
    {
        [RosProp(DisplayName = "底板 IO 节点名", DefaultValue = "motion_bus_io_0", Description = "指定向哪个底板发送 IO 请求")]
        public string IoBoardNodeName { get; set; } = "motion_bus_io_0";

        // ==========================================
        // 极致优雅：为属性打上 [IoTagSelector]
        // ==========================================
        [RosProp(DisplayName = "工作位输入标签", DefaultValue = "DI_CYL_A_WORK")]
        [IoTagSelector]
        public string InTagWorkPos { get; set; } = "DI_CYL_A_WORK";

        [RosProp(DisplayName = "复位位输入标签", DefaultValue = "DI_CYL_A_RESET")]
        [IoTagSelector]
        public string InTagResetPos { get; set; } = "DI_CYL_A_RESET";

        [RosProp(DisplayName = "输出至工作位标签", DefaultValue = "DO_CYL_A_PUSH")]
        [IoTagSelector]
        public string OutTagToWork { get; set; } = "DO_CYL_A_PUSH";

        [RosProp(DisplayName = "输出至复位位标签", DefaultValue = "DO_CYL_A_PULL")]
        [IoTagSelector]
        public string OutTagToReset { get; set; } = "DO_CYL_A_PULL";

        // RPC 客户端
        private RosServiceClient<SetIoReq, SetIoRes>? _setIoClient;
        private RosServiceClient<GetIoReq, GetIoRes>? _getIoClient;

        public PneumaticCylinderNode(INatsClient nats, string nodeName, ILogger<PneumaticCylinderNode> logger)
            : base(nats, nodeName, logger)
        {
        }

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 从 Parameter Server 读取配置
            IoBoardNodeName = Parameters.GetLocal("IoBoardNodeName", "motion_bus_io_0");
            InTagWorkPos = Parameters.GetLocal("InTagWorkPos", "DI_CYL_A_WORK");
            InTagResetPos = Parameters.GetLocal("InTagResetPos", "DI_CYL_A_RESET");
            OutTagToWork = Parameters.GetLocal("OutTagToWork", "DO_CYL_A_PUSH");
            OutTagToReset = Parameters.GetLocal("OutTagToReset", "DO_CYL_A_PULL");

            // 初始化跨节点 RPC 通信客户端 (气缸去呼叫底层 IO 板卡)
            _setIoClient = CreateClient<SetIoReq, SetIoRes>($"{IoBoardNodeName}.io.set");
            _getIoClient = CreateClient<GetIoReq, GetIoRes>($"{IoBoardNodeName}.io.get");

            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🔩 气缸驱动节点 [{NodeName}] 已启动，正在监听网络控制指令...", Name);

            var moveServer = CreateActionServer<CylinderMoveGoal, CylinderMoveFeedback, CylinderMoveResult>($"{Name}.move");

            _ = moveServer.ServeAsync(async (goal, feedback, actionCt) =>
            {
                if (_setIoClient == null || _getIoClient == null) return new CylinderMoveResult(false, "IO客户端未初始化");

                Logger.LogInformation("🔩 气缸 [{NodeName}] 开始移动 -> 目标: {Target}", Name, goal.TargetState);

                // 根据目标状态，获取对应的标签名
                bool isWorkPosTarget = goal.TargetState == CylinderState.WorkPos;
                string outputTagOn = isWorkPosTarget ? OutTagToWork : OutTagToReset;
                string outputTagOff = isWorkPosTarget ? OutTagToReset : OutTagToWork;
                string inputTagCheck = isWorkPosTarget ? InTagWorkPos : InTagResetPos;

                try
                {
                    // 1. 安全逻辑：先关闭相反方向的电磁阀，防止双端通气互锁
                    await _setIoClient.CallAsync(new SetIoReq(outputTagOff, false), TimeSpan.FromSeconds(1), actionCt);
                    await Task.Delay(50, actionCt); // 物理换向缓冲时间

                    // 2. 激活目标方向的电磁阀
                    await _setIoClient.CallAsync(new SetIoReq(outputTagOn, true), TimeSpan.FromSeconds(1), actionCt);
                    feedback(new CylinderMoveFeedback(goal.TargetState, "电磁阀已动作，等待传感器..."));

                    // 3. 阻塞等待输入传感器信号 (带 Timeout 机制)
                    Stopwatch sw = Stopwatch.StartNew();
                    bool isArrived = false;

                    while (!stoppingToken.IsCancellationRequested && !actionCt.IsCancellationRequested)
                    {
                        // 发起网络查询传感器的状态
                        var getRes = await _getIoClient.CallAsync(new GetIoReq(inputTagCheck), TimeSpan.FromSeconds(1), actionCt);

                        if (getRes != null && getRes.Success && getRes.State == true)
                        {
                            isArrived = true;
                            break; // 传感器亮了，说明到位了！
                        }

                        // 超时判断
                        if (sw.ElapsedMilliseconds >= goal.TimeoutMs)
                        {
                            throw new Exception($"气缸运动超时({goal.TimeoutMs}ms)，未收到传感器 [{inputTagCheck}] 信号！");
                        }

                        await Task.Delay(20, actionCt); // 每 20ms 查询一次
                    }
                    actionCt.ThrowIfCancellationRequested(); // 检查是否被外部紧急取消

                    if (isArrived)
                    {
                        Logger.LogInformation("✅ 气缸 [{NodeName}] 移动成功到位 -> {State}", Name, goal.TargetState);
                        return new CylinderMoveResult(true, "移动到位");
                    }

                    return new CylinderMoveResult(false, "未知原因未到位");
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("⚠️ 气缸 [{NodeName}] 移动被中途打断！", Name);
                    // 发生急停时，把所有输出关闭
                    _ = _setIoClient.CallAsync(new SetIoReq(OutTagToWork, false));
                    _ = _setIoClient.CallAsync(new SetIoReq(OutTagToReset, false));
                    return new CylinderMoveResult(false, "移动被急停取消");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "❌ 气缸 [{NodeName}] 运动异常！触发 AEM 报警", Name);

                    // 【高光时刻】：底层驱动一旦报错，立刻往系统里扔一个致命报警！
                    // 这个消息会瞬间被 AEM 捕获 -> 大屏闪红灯 -> L1 行为树挂起
                    _ = Nats.PublishAsync("aem.raise", new RaiseAlarmMsg("ERR_CYL_001", new[] { Name, ex.Message }));

                    return new CylinderMoveResult(false, ex.Message);
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}