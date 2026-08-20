using Hexiv.BehaviorTree.Attributes;
using Hexiv.BehaviorTree.Core;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using NatsROS.Messages.Hardware;
using NatsROS.Messages.Process;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.MissionControl.MainDirector.Actions
{

    /// <summary>
    /// 积木 1：异步等待 IO 信号 (取代死循环轮询)
    /// </summary>
    [BtNode(DisplayName = "1. 挂起等待 IO 信号", Category = "多机协同", Description = "异步等待指定的 IO 达到目标状态，零 CPU 消耗")]
    public class WaitIoAction : BtActionBase
    {
        [BtProp(DisplayName = "IO 板卡节点名", DefaultValue = "motionboard_1")]
        public string BoardNodeName { get; set; } = "motionboard_1";

        [BtProp(DisplayName = "引脚号 (Pin)", DefaultValue = "0")]
        public int Pin { get; set; } = 0;

        [BtProp(DisplayName = "目标状态 (True=高电平)", DefaultValue = "True")]
        public bool TargetState { get; set; } = true;

        public WaitIoAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");
            var getIoClient = new RosServiceClient<GetIoReq, GetIoRes>(nats, $"{BoardNodeName}.io.get");

            // 每隔 100ms 查一次，因为 Task.Delay 会释放线程，所以几乎不消耗 CPU
            while (!ct.IsCancellationRequested)
            {
                var res = await getIoClient.CallAsync(new GetIoReq(Pin), TimeSpan.FromSeconds(1), ct);
                if (res != null && res.Success && res.State == TargetState)
                {
                    return BtNodeStatus.Success; // 信号到了，放行！
                }
                await Task.Delay(100, ct);
            }
            return BtNodeStatus.Failure;
        }
    }


    /// <summary>
    /// 积木 2：Y1 / Y2 派发订单 (请求方)
    /// </summary>
    [BtNode(DisplayName = "2. 派发加工订单 (Y轴用)", Category = "多机协同", Description = "向点胶中心派发任务，并挂起等待加工完成")]
    public class DispatchWorkOrderAction : BtActionBase
    {
        [BtProp(DisplayName = "加工中心路由", DefaultValue = "dispenser_center.order")]
        public string TargetRoute { get; set; } = "dispenser_center.order";

        [BtProp(DisplayName = "等待超时 (秒)", DefaultValue = "60")]
        public int TimeoutSeconds { get; set; } = 60;

        public DispatchWorkOrderAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");

            // 从黑板拿到刚才扫码枪扫出的条码
            string barcode = blackboard.Get("Barcode", out string b) ? b : "UNKNOWN";

            var orderClient = new RosServiceClient<WorkOrderReq, WorkOrderRes>(nats, TargetRoute);

            // 发起 RPC 并挂起等待。这里设置长超时，因为点胶可能需要几十秒！
            var res = await orderClient.CallAsync(new WorkOrderReq(Name, barcode), TimeSpan.FromSeconds(TimeoutSeconds), ct);

            if (res != null && res.Success)
            {
                return BtNodeStatus.Success;
            }

            return BtNodeStatus.Failure;
        }
    }


    /// <summary>
    /// 积木 3：点胶中心接单 (接收方 - 阻塞等待)
    /// </summary>
    [BtNode(DisplayName = "3. 挂起接收订单 (点胶机用)", Category = "多机协同", Description = "阻塞等待外部发来的工作订单，一旦收到立即唤醒并执行后续工艺")]
    public class AcceptWorkOrderAction : BtActionBase
    {
        [BtProp(DisplayName = "监听的订单路由", DefaultValue = "dispenser_center.order")]
        public string ListenRoute { get; set; } = "dispenser_center.order";

        public AcceptWorkOrderAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");

            // NATS 的极致优雅：开启订阅流！
            var orderSub = nats.SubscribeAsync<WorkOrderReq>(ListenRoute, cancellationToken: ct);

            // await foreach 会将当前节点“深度冻结”，直到有人往这个地址发了消息，它才会苏醒进循环！
            await foreach (var msg in orderSub)
            {
                if (msg.Data != null && !string.IsNullOrEmpty(msg.ReplyTo))
                {
                    // 【神来之笔】：把 NATS 自动生成的“回执地址(ReplyTo)”存入黑板！
                    // 这样即使 Y1 和 Y2 交替发单，点胶机也绝对不会回错人！
                    blackboard.Set("CurrentReplyTo", msg.ReplyTo);
                    blackboard.Set("Barcode", msg.Data.Barcode);
                    blackboard.Set("SourceStation", msg.Data.SourceStation);

                    return BtNodeStatus.Success; // 接到单了，树继续往下走！
                }
            }
            return BtNodeStatus.Failure;
        }
    }


    /// <summary>
    /// 积木 4：点胶中心回执 (接收方 - 加工完成)
    /// </summary>
    [BtNode(DisplayName = "4. 回复加工完成 (点胶机用)", Category = "多机协同", Description = "点胶工序完成后，按原路将完成信号发送给呼叫方")]
    public class ReplyWorkOrderAction : BtActionBase
    {
        public ReplyWorkOrderAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");

            // 从黑板中拿出接单时存入的那个唯一的“回执地址”
            if (!blackboard.Get("CurrentReplyTo", out string replyTo) || string.IsNullOrEmpty(replyTo))
            {
                return BtNodeStatus.Failure; // 找不到回执地址，说明没接过单
            }

            var resMsg = new WorkOrderRes(true, "加工圆满完成");

            // 顺着网线原路拍回去！Y1 轴瞬间解冻苏醒！
            await nats.PublishAsync(replyTo, resMsg, cancellationToken: ct);

            return BtNodeStatus.Success;
        }
    }
}