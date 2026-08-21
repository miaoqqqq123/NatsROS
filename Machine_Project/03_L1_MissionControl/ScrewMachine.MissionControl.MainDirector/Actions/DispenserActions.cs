using Hexiv.BehaviorTree.Attributes;
using Hexiv.BehaviorTree.Core;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using NatsROS.Messages.GeometryMsgs;
using NatsROS.Messages.MES;
using NatsROS.Messages.Motion;
using NatsROS.Messages.SensorMsgs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.MissionControl.MainDirector.Actions
{
    [BtNode(DisplayName = "1. 视觉 Mark 定位", Category = "点胶工艺", Description = "呼叫相机获取产品的偏移量，并存入黑板")]
    public class VisionLocateAction : BtActionBase
    {
        public VisionLocateAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入黑板");
            var visionClient = new RosServiceClient<FindMarkReq, FindMarkRes>(nats, "simulatedvisionnode_1.find_mark");

            var res = await visionClient.CallAsync(new FindMarkReq(), TimeSpan.FromSeconds(5), ct);
            if (res == null || !res.Success) return BtNodeStatus.Failure;

            var isVal = Random.Shared.NextDouble();
            if (isVal < 0.8)
            {
                //Logger.LogWarning("⚠️ [模拟视觉异常] 光线反光，无法提取 Mark 点轮廓！");
                // 返回失败结果
                return BtNodeStatus.Failure;
            }

            // 将视觉算出来的偏差，写进黑板，供后面的节点使用！
            blackboard.Set("OffsetX", res.OffsetX);
            blackboard.Set("OffsetY", res.OffsetY);
            blackboard.Set("Angle", res.AngleDegree);

            return BtNodeStatus.Success;
        }
    }

    [BtNode(DisplayName = "2. 纠偏与矩形点胶", Category = "点胶工艺", Description = "读取黑板偏移量，修正轨迹并驱动三轴联动点胶")]
    public class DispenseRectAction : BtActionBase
    {
        [BtProp(DisplayName = "点胶速度 (mm/s)", DefaultValue = "30")]
        public double Velocity { get; set; } = 30;

        public DispenseRectAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入黑板");

            if (!blackboard.Get("OffsetX", out double ox)) ox = 0;
            if (!blackboard.Get("OffsetY", out double oy)) oy = 0;
            if (!blackboard.Get("Angle", out double ang)) ang = 0;

            var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.move");
            var trajClient = new RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.trajectory");
            var valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(nats, "simulateddispensernode_1.valve");

            var standardPath = new List<Vector3>
            {
                new Vector3(-30, 30, 0), new Vector3(30, 30, 0), new Vector3(30, -30, 0),
                new Vector3(-30, -30, 0), new Vector3(-30, 30, 0)
            };

            var correctedPath = new List<Vector3>();
            double rad = ang * Math.PI / 180.0;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);

            foreach (var pt in standardPath)
            {
                double newX = pt.X * cosA - pt.Y * sinA + ox;
                double newY = pt.X * sinA + pt.Y * cosA + oy;
                correctedPath.Add(new Vector3(newX, newY, pt.Z));
            }

            var startPt = correctedPath[0];

            await moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 20, 50), null, ct);
            await moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 0, 20), null, ct);

            await valveClient.CallAsync(new ValveControlReq(true), TimeSpan.FromSeconds(2), ct);
            await trajClient.SendGoalAsync(new DispenserTrajectoryGoal(correctedPath.ToArray(), Velocity), null, ct);
            await valveClient.CallAsync(new ValveControlReq(false), TimeSpan.FromSeconds(2), ct);

            await moveClient.SendGoalAsync(new DispenserMoveGoal(startPt.X, startPt.Y, 20, 50), null, ct);
            await moveClient.SendGoalAsync(new DispenserMoveGoal(0, 0, 50, 50), null, ct);

            return BtNodeStatus.Success;
        }
    }
    
    [BtNode(DisplayName = "3. 上传 MES 记录", Category = "点胶工艺", Description = "点胶结束后，将结果发送给 MES 节点")]
    public class UploadMesAction : BtActionBase
    {
        // 由工艺员在界面上决定把数据发给谁
        [BtProp(DisplayName = "目标 MES 节点名", DefaultValue = "mockmesnode_1")]
        public string MesNodeName { get; set; } = "mockmesnode_1";

        public UploadMesAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入黑板");

            // 动态拼接路由，绝不硬编码！
            var mesClient = new RosServiceClient<UploadRecordReq, UploadRecordRes>(nats, $"{MesNodeName}.upload");

            blackboard.Get("OffsetX", out double ox);
            blackboard.Get("OffsetY", out double oy);
            blackboard.Get("Angle", out double ang);

            // 【核心】：优先使用工人扫进来的条码
            if (!blackboard.Get("Barcode", out string barcode))
                barcode = $"SN-AUTO-{DateTime.Now:yyyyMMddHHmmss}";

            // 【核心适配】：采用 record 的主构造函数，摒弃大括号！
            var record = new ProductRecord(
                Barcode: barcode,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsPass: true,
                CycleTimeSec: Random.Shared.NextDouble()*10, // 演示时间
                OffsetX: ox,
                OffsetY: oy,
                Angle: ang
            );

            // 呼叫目标 MES 节点，如果对方不在，这里依然会抛出超时异常 (被父类捕获为 Failure)
            await mesClient.CallAsync(new UploadRecordReq(record), TimeSpan.FromSeconds(2), ct);
            return BtNodeStatus.Success;
        }
    }


    /// <summary>
    /// 积木 4：安全退回原点 (异常兜底)
    /// </summary>
    [BtNode(DisplayName = "4. 安全退回原点 (Alarm)", Category = "异常处理", Description = "当严重异常发生时触发，安全关闭胶阀并抬起针头")]
    public class SafeHomeAction : BtActionBase
    {
        public SafeHomeAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");
            var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.move");
            var valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(nats, "simulateddispensernode_1.valve");

            // 无论刚才发生了什么，强制关胶！
            await valveClient.CallAsync(new ValveControlReq(false), TimeSpan.FromSeconds(2), ct);

            // 安全抬起到最高点 (Z=50)
            await moveClient.SendGoalAsync(new DispenserMoveGoal(0, 0, 50, 50), null, ct);

            // 实际工业场景中，这里还可以发 NATS 消息点亮三色灯的红灯、触发蜂鸣器等
            return BtNodeStatus.Success;
        }
    }
}