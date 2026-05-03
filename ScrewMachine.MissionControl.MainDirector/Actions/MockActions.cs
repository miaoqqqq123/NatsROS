using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Hexiv.BehaviorTree.Core;
using Hexiv.BehaviorTree.Attributes; // 保留用来做类识别的 [BtNode]

namespace ScrewMachine.MissionControl.MainDirector.Actions
{

    /// <summary>
    /// 纯血 .NET 原生特性的行为树节点示例
    /// </summary>
    [BtNode(DisplayName = "呼叫视觉定位", Category = "视觉技能", Description = "呼叫 L2 层进行产品拍照和位置计算")]
    public class VisionFindHoleAction : BtActionBase
    {
        public VisionFindHoleAction(string name) : base(name) { }

        // 【极其优雅】：全面使用 C# 原生特性！
        [Category("视觉参数")]
        [DisplayName("匹配阈值 (0-1)")]
        [Description("模板匹配的最低置信度阈值。")]
        [DefaultValue(0.85)]
        public double Threshold { get; set; } = 0.85;

        protected override async Task<NodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            // 在实际代码中，这里通过 NATS RPC 呼叫底层
            await Task.Delay(800, ct);
            return NodeStatus.Success;
        }
    }

    [BtNode(DisplayName = "呼叫电批打紧", Category = "执行技能", Description = "下达具体扭矩指令执行拧紧动作")]
    public class DriveScrewAction : BtActionBase
    {
        public DriveScrewAction(string name) : base(name) { }

        [Category("运动参数")]
        [DisplayName("目标扭矩 (N.m)")]
        [Description("当达到此扭矩时，视为打紧成功。")]
        [DefaultValue(1.5)]
        public double TargetTorque { get; set; } = 1.5;

        [Category("运动参数")]
        [DisplayName("转速 (RPM)")]
        [DefaultValue(800)]
        public int Rpm { get; set; } = 800;

        protected override async Task<NodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            await Task.Delay(1500, ct);

            // 故意模拟高扭矩滑牙
            if (TargetTorque > 2.0) return NodeStatus.Failure;

            return NodeStatus.Success;
        }
    }
}