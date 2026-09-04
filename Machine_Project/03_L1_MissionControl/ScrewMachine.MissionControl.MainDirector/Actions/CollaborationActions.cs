using Hexiv.BehaviorTree.Attributes;
using Hexiv.BehaviorTree.Core;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using ScrewMachine.Messages.Hardware;
using ScrewMachine.Messages.Process;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Channels;
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

        [BtProp(DisplayName = "引脚号 (Pin)", DefaultValue = "0", Description = "要监控的引脚号")]
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

        [BtProp(DisplayName = "本工站代号 (如 Y1/Y2)", DefaultValue = "Y1")]
        public string StationCode { get; set; } = "Y1";

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
            var res = await orderClient.CallAsync(new WorkOrderReq(StationCode, barcode), TimeSpan.FromSeconds(TimeoutSeconds), ct);

            if (res != null && res.Success)
            {
                return BtNodeStatus.Success;
            }

            return BtNodeStatus.Failure;
        }
    }


    /// <summary>
    /// 积木 3：点胶中心接单 (接收方 - 阻塞等待 & FIFO 队列)
    /// </summary>
    [BtNode(DisplayName = "3. 挂起接收订单 (点胶机用)", Category = "多机协同", Description = "后台持续监听订单并排队。执行时从队列中取出一个订单，先进先出。")]
    public class AcceptWorkOrderAction : BtActionBase
    {
        [BtProp(DisplayName = "监听的订单路由", DefaultValue = "dispenser_center.order")]
        public string ListenRoute { get; set; } = "dispenser_center.order";

        // 【核心魔法 1】：全局静态无锁队列，专门用来存订单和它的回执地址！
        private static readonly Channel<(WorkOrderReq Req, string ReplyTo)> _orderQueue = Channel.CreateUnbounded<(WorkOrderReq, string)>();

        // 用于保证后台监听协程只被启动一次
        private static int _listenerStarted = 0;

        public AcceptWorkOrderAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");

            // 【核心魔法 2】：如果这是程序开机后第一次执行该节点，立刻启动后台接单协程！
            if (Interlocked.Exchange(ref _listenerStarted, 1) == 0)
            {
                // 这个 Task 会在后台永远运行，疯狂把网上的订单塞进 _orderQueue 队列里
                _ = Task.Run(async () =>
                {
                    var sub = nats.SubscribeAsync<WorkOrderReq>(ListenRoute);
                    await foreach (var msg in sub)
                    {
                        if (msg.Data != null && !string.IsNullOrEmpty(msg.ReplyTo))
                        {
                            // 订单进入 Channel 队列排队
                            await _orderQueue.Writer.WriteAsync((msg.Data, msg.ReplyTo));
                        }
                    }
                });
            }

            // 【核心魔法 3】：节点真正的执行逻辑，变成了从队列里“拿”订单。
            // 如果队列是空的，它会在这里优雅地挂起等待 (不会消耗任何 CPU)。
            // 如果队列里有多个，它必定拿到最先进来的那一个！
            var order = await _orderQueue.Reader.ReadAsync(ct);

            // 拿到订单后，存入黑板，供后面的视觉和点胶节点使用
            blackboard.Set("CurrentReplyTo", order.ReplyTo);
            blackboard.Set("Barcode", order.Req.Barcode);
            blackboard.Set("SourceStation", order.Req.SourceStation); // Y1 还是 Y2？

            return BtNodeStatus.Success;
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

            // 2. 【核心优化】：从黑板中读取真正的加工结果！
            // 如果前面的节点（视觉、点胶）失败了，可以通过黑板把 IsPass 置为 false
            // 默认情况下如果没有人改它，我们认为是良品 (True)
            bool isPass = blackboard.Get("IsPass", out bool p) ? p : true;
            string msg = isPass ? "加工圆满完成" : "加工异常中断";

            var resMsg = new WorkOrderRes(isPass, msg);

            // 3. 顺着网线原路拍回去！通知 Y 轴
            await nats.PublishAsync(replyTo, resMsg, cancellationToken: ct);

            // ==========================================
            // 4. 【核心防御】：过河拆桥，彻底销毁订单痕迹！
            // 保证不管下一次发生什么灵异事件，绝对不会重复发回执！
            // ==========================================
            blackboard.Set("CurrentReplyTo", string.Empty);
            blackboard.Set("Barcode", string.Empty);
            blackboard.Set("SourceStation", string.Empty);
            // 顺便把结果标记也复位，准备迎接下一单
            blackboard.Set("IsPass", true);

            return BtNodeStatus.Success;
        }
    }
}