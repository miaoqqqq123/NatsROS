using Hexiv.BehaviorTree.Attributes;
using Hexiv.BehaviorTree.Core;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using NatsROS.Messages.GeometryMsgs;
using ScrewMachine.Messages.Motion;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.MissionControl.MainDirector.Actions
{
    /// <summary>
    /// 智能积木 1：移动到命名点位 (支持机器点/配方点)
    /// </summary>
    [BtNode(DisplayName = "1. 移动到命名点位 (离散单点)", Category = "运动控制", Description = "通过点位名称自动查询并移动。如果是配方点，将自动叠加视觉纠偏。")]
    public class MoveToNamedPointAction : BtActionBase
    {
        [BtProp(DisplayName = "目标点位名称")]
        [PointSelector] // 🌟 触发大屏 UI 的下拉框黑魔法！
        public string TargetPointName { get; set; } = "PurgePos";

        [BtProp(DisplayName = "数据来源 (Machine=全局, Product=配方)")]
        public PointSource Source { get; set; } = PointSource.Machine;

        public MoveToNamedPointAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");
            string sourceStation = blackboard.Get("SourceStation", out string st) && !string.IsNullOrEmpty(st) ? st : "Y1";

            // 1. 根据来源，去全网或黑板拉取 JSON 数据
            string ptJson = "";
            if (Source == PointSource.Machine)
            {
                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(nats, "brain_dispenser");
                ptJson = await paramClient.GetAsync($"MachinePoint_{TargetPointName}", ct) ?? "";
            }
            else
            {
                ptJson = blackboard.Get($"RecipePoint_{TargetPointName}", out string rpt) ? rpt : "";
            }

            if (string.IsNullOrEmpty(ptJson)) throw new Exception($"未找到点位数据: {TargetPointName}");

            // 2. 【核心多态解析】：利用 .NET 8 JsonDerivedType，自动识别为 SinglePointModel！
            var baseModel = JsonSerializer.Deserialize<PointFeatureBase>(ptJson);
            if (baseModel is not SinglePointModel pt) throw new Exception($"点位 {TargetPointName} 不是一个合法的单点模型！");

            double targetX = pt.X, targetY = pt.Y;

            // 3. 如果是产品配方点，执行强大的运动学 2D 仿射变换！
            if (Source == PointSource.Product)
            {
                double ox = blackboard.Get("OffsetX", out double oxVal) ? oxVal : 0;
                double oy = blackboard.Get("OffsetY", out double oyVal) ? oyVal : 0;
                double ang = blackboard.Get("Angle", out double angVal) ? angVal : 0;

                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(nats, "brain_dispenser");
                double baseX = double.Parse(await paramClient.GetAsync($"{sourceStation}_BaseX", ct) ?? "0");
                double baseY = double.Parse(await paramClient.GetAsync($"{sourceStation}_BaseY", ct) ?? "0");

                // 【DRY原则】：呼叫数学助手类
                var worldCoords = KinematicsHelper.TransformToWorld(pt.X, pt.Y, ox, oy, ang, baseX, baseY);
                targetX = worldCoords.WorldX;
                targetY = worldCoords.WorldY;
            }

            // 4. 下发到底层插补器 (必须带上 TargetYAxis 身份证)
            var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.move");
            await moveClient.SendGoalAsync(new DispenserMoveGoal(targetX, targetY, pt.Z, pt.Speed, sourceStation), null, ct);

            return BtNodeStatus.Success;
        }
    }

    /// <summary>
    /// 智能积木 2：执行连续轨迹 (微型 G-Code 解析器)
    /// </summary>
    [BtNode(DisplayName = "2. 执行命名轨迹 (连续点)", Category = "运动控制", Description = "从配方中提取连续轨迹，自动进行视觉纠偏、平滑插补并智能开关胶阀。")]
    public class ExecuteTrajectoryAction : BtActionBase
    {
        [BtProp(DisplayName = "轨迹代号")]
        [PointSelector] // 🌟 触发大屏 UI 的下拉框黑魔法！
        public string TrajectoryName { get; set; } = "GluePath_A";

        [BtProp(DisplayName = "数据来源 (Machine=全局, Product=配方)")]
        public PointSource Source { get; set; } = PointSource.Product; // 轨迹通常来自配方

        public ExecuteTrajectoryAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            var nats = blackboard.Get<INatsClient>("Nats") ?? throw new Exception("NATS 未注入");
            string sourceStation = blackboard.Get("SourceStation", out string st) && !string.IsNullOrEmpty(st) ? st : "Y1";

            // 1. 获取轨迹数据
            string trajJson = "";
            if (Source == PointSource.Product)
                trajJson = blackboard.Get($"RecipeTraj_{TrajectoryName}", out string tj) ? tj : "";
            else
                trajJson = await new NatsROS.Core.Parameters.RosParameterClient(nats, "brain_dispenser").GetAsync($"MachineTraj_{TrajectoryName}", ct) ?? "";

            if (string.IsNullOrEmpty(trajJson)) throw new Exception($"未找到轨迹数据: {TrajectoryName}");

            // 2. 【多态解析】
            var baseModel = JsonSerializer.Deserialize<PointFeatureBase>(trajJson);
            if (baseModel is not TrajectoryModel traj || traj.Nodes.Count == 0) return BtNodeStatus.Failure;

            // 3. 准备仿射变换的基准数据
            double ox = 0, oy = 0, ang = 0, baseX = 0, baseY = 0;
            if (Source == PointSource.Product)
            {
                ox = blackboard.Get("OffsetX", out double oxVal) ? oxVal : 0;
                oy = blackboard.Get("OffsetY", out double oyVal) ? oyVal : 0;
                ang = blackboard.Get("Angle", out double angVal) ? angVal : 0;

                var paramClient = new NatsROS.Core.Parameters.RosParameterClient(nats, "brain_dispenser");
                baseX = double.Parse(await paramClient.GetAsync($"{sourceStation}_BaseX", ct) ?? "0");
                baseY = double.Parse(await paramClient.GetAsync($"{sourceStation}_BaseY", ct) ?? "0");
            }

            var moveClient = new RosActionClient<DispenserMoveGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.move");
            var trajClient = new RosActionClient<DispenserTrajectoryGoal, DispenserMoveFeedback, DispenserMoveResult>(nats, "simulateddispensernode_1.trajectory");
            var valveClient = new RosServiceClient<ValveControlReq, ValveControlRes>(nats, "simulateddispensernode_1.valve");

            bool isValveCurrentlyOpen = false;
            var currentSegment = new List<Vector3>();
            double currentSpeed = traj.Nodes[0].Speed;

            // 4. 安全开到轨迹起点上方 20mm
            var startPt = traj.Nodes[0];
            var stWorld = KinematicsHelper.TransformToWorld(startPt.X, startPt.Y, ox, oy, ang, baseX, baseY);

            await moveClient.SendGoalAsync(new DispenserMoveGoal(stWorld.WorldX, stWorld.WorldY, startPt.Z + 20, 100, sourceStation), null, ct);
            await moveClient.SendGoalAsync(new DispenserMoveGoal(stWorld.WorldX, stWorld.WorldY, startPt.Z, startPt.Speed, sourceStation), null, ct);

            // 5. 【高能预警：仿 CNC 智能片段解析引擎】
            foreach (var pt in traj.Nodes)
            {
                var wCoords = KinematicsHelper.TransformToWorld(pt.X, pt.Y, ox, oy, ang, baseX, baseY);

                // 如果发现该点的吐胶状态变化了，立刻打断当前路线，操作 IO 阀门！
                if (pt.IsDispense != isValveCurrentlyOpen)
                {
                    if (currentSegment.Count > 0)
                    {
                        await trajClient.SendGoalAsync(new DispenserTrajectoryGoal(currentSegment.ToArray(), currentSpeed, sourceStation), null, ct);
                        currentSegment.Clear();
                    }

                    isValveCurrentlyOpen = pt.IsDispense;
                    await valveClient.CallAsync(new ValveControlReq(isValveCurrentlyOpen), TimeSpan.FromSeconds(2), ct);
                }

                currentSpeed = pt.Speed;
                currentSegment.Add(new Vector3(wCoords.WorldX, wCoords.WorldY, pt.Z));
            }

            // 把最后剩下的一段扫尾走完
            if (currentSegment.Count > 0)
                await trajClient.SendGoalAsync(new DispenserTrajectoryGoal(currentSegment.ToArray(), currentSpeed, sourceStation), null, ct);

            // 安全关胶并抬起针头
            if (isValveCurrentlyOpen)
                await valveClient.CallAsync(new ValveControlReq(false), TimeSpan.FromSeconds(2), ct);
            await moveClient.SendGoalAsync(new DispenserMoveGoal(stWorld.WorldX, stWorld.WorldY, startPt.Z + 20, 100, sourceStation), null, ct);

            return BtNodeStatus.Success;
        }
    }
}