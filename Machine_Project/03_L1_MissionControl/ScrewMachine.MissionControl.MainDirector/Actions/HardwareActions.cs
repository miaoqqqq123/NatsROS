using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using ScrewMachine.Messages.Hardware;
using ScrewMachine.Messages.Motion;
using System;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.BehaviorTree.Attributes;
using NatsROS.BehaviorTree.Core;
using NatsROS.Messages.Motion;

namespace ScrewMachine.MissionControl.MainDirector.Actions
{
    /// <summary>
    /// 积木：通用单轴移动 (供 Y1/Y2 等工位进出站使用)
    /// </summary>
    [BtNode(DisplayName = "单轴移动", Category = "运动控制", Description = "控制指定的单轴移动到绝对坐标位置")]
    public class AxisMoveAction : BtActionBase
    {
        [BtProp(DisplayName = "目标轴节点名", DefaultValue = "axis_y1", Description = "在 launch.json 或大盘中拉起的轴卡名字")]
        public string TargetAxis { get; set; } = "axis_y1";

        [BtProp(DisplayName = "目标位置 (mm)", DefaultValue = "500.0")]
        public double TargetPosition { get; set; } = 500.0;

        [BtProp(DisplayName = "移动速度 (mm/s)", DefaultValue = "100.0")]
        public double Velocity { get; set; } = 100.0;

        public AxisMoveAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");

            // 动态路由：呼叫指定的轴
            var moveClient = new RosActionClient<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult>(nats, $"{TargetAxis}.move");

            var res = await moveClient.SendGoalAsync(new AxisMoveGoal(TargetPosition, Velocity), null, ct);

            if (res != null && res.Data.IsSuccess) return BtNodeStatus.Success;
            return BtNodeStatus.Failure;
        }
    }


    /// <summary>
    /// 积木：扫码枪触发
    /// </summary>
    [BtNode(DisplayName = "扫码枪触发", Category = "数据采集", Description = "软触发扫码枪，并将条码数据存入大脑黑板")]
    public class TriggerScanAction : BtActionBase
    {
        [BtProp(DisplayName = "扫码枪节点名", DefaultValue = "scanner_y1")]
        public string ScannerNode { get; set; } = "scanner_y1";

        [BtProp(DisplayName = "超时时间 (ms)", DefaultValue = "3000")]
        public int TimeoutMs { get; set; } = 3000;

        public TriggerScanAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");
            var scanClient = new RosServiceClient<TriggerScanReq, TriggerScanRes>(nats, $"{ScannerNode}.trigger");

            // 呼叫 L3 的扫码枪硬件
            var res = await scanClient.CallAsync(new TriggerScanReq(TimeoutMs), TimeSpan.FromMilliseconds(TimeoutMs + 500), ct);

            if (res != null && res.Success && !string.IsNullOrEmpty(res.Barcode))
            {
                // 【极其关键】：把扫到的条码存入黑板！
                // 这样后续的“派发加工订单”和“MES 上传”就能直接拿到这个条码了！
                blackboard.Set("Barcode", res.Barcode);
                return BtNodeStatus.Success;
            }
            return BtNodeStatus.Failure;
        }
    }

    /// <summary>
    /// 积木：MES 进站校验 (防呆)
    /// </summary>
    [BtNode(DisplayName = "MES 进站校验", Category = "数据采集", Description = "检查黑板条码的合法性，防止重复加工或错漏")]
    public class MesCheckInAction : BtActionBase
    {
        public MesCheckInAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            // 1. 确保黑板里有条码 (刚刚扫码枪放进去的)
            if (!blackboard.Get("Barcode", out string barcode) || string.IsNullOrEmpty(barcode))
            {
                return BtNodeStatus.Failure;
            }

            // 2. 真实场景中，这里应该通过 NATS 呼叫 MES 节点进行 HTTP/API 校验
            // 比如检查该产品上一道工序做了没有，是否已经是报废品等。
            // 这里我们用 Delay 模拟秒级网络校验
            await Task.Delay(200, ct);

            // 防呆校验通过
            return BtNodeStatus.Success;
        }
    }
}