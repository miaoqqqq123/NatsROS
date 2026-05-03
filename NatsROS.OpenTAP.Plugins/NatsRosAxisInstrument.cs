using OpenTap;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.Serialization;
using NatsROS.Messages.Motion;
using System;

namespace NatsROS.OpenTAP.Plugins
{
    [Display("NatsROS 虚拟单轴", Group: "NatsROS Instruments", Description: "跨网络调用的物理轴代理")]
    public class NatsRosAxisInstrument : Instrument
    {
        [Display("NATS 网络地址")]
        public string NatsUrl { get; set; } = "nats://127.0.0.1:4222"; [Display("动作寻址路由 (需与底层节点名匹配)")]
        public string ActionRoute { get; set; } = "axis_x.move";

        private INatsClient _nats;
        private RosActionClient<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult> _moveClient;

        public NatsRosAxisInstrument() { Name = "Axis_X_Proxy"; }

        public override void Open()
        {
            base.Open();
            var options = NatsOpts.Default with { Url = NatsUrl, SerializerRegistry = new NatsRosSerializerRegistry() };
            _nats = new NatsClient(options);
            _nats.ConnectAsync().GetAwaiter().GetResult();
            _moveClient = new RosActionClient<AxisMoveGoal, AxisMoveFeedback, AxisMoveResult>(_nats, ActionRoute);
        }

        public override void Close()
        {
            _nats?.DisposeAsync().GetAwaiter().GetResult();
            base.Close();
        }

        // 提供给 Step 的同步阻塞调用方法
        public double MoveTo(double targetPos, double velocity, int timeoutSec = 60)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                // 瞬间转化为 NATS 的异步长任务，并挂载一个回调，把底层的高频反馈直接打印到 OpenTAP 日志里！
                var result = _moveClient.SendGoalAsync(
                    new AxisMoveGoal(targetPos, velocity),
                    feedback => Log.Debug($"[编码器实时反馈] 当前位置: {feedback.CurrentPosition:F2} mm"),
                    cts.Token // 传入令牌！
                ).GetAwaiter().GetResult();

                // 校验双重状态：1. RPC 动作本身是否成功 2. 底层物理轴返回的业务结果是否成功
                if (result == null || result.Status != ActionStatus.Succeeded || !result.Data.IsSuccess)
                {
                    throw new Exception($"轴移动失败或被急停打断！网络状态: {result?.Status}, 物理反馈: {result?.Data.Message}");
                }

                return result.Data.FinalPosition;
            }
            catch (OperationCanceledException)
            {
                // 如果时间到了还没走完，cts.Token 会触发取消异常
                throw new Exception($"[NatsROS] 轴移动动作严重超时！({timeoutSec} 秒未完成)");
            }
        }
    }
}