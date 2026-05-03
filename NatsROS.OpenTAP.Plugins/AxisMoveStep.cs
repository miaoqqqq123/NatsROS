using OpenTap;

namespace NatsROS.OpenTAP.Plugins
{
    [Display("移动轴到指定位置", Group: "NatsROS Steps")]
    public class AxisMoveStep : TestStep
    {
        [Display("目标轴", Order: 1)]
        public NatsRosAxisInstrument Axis { get; set; }
        [Display("目标位置 (mm)", Order: 2)]
        public double TargetPos { get; set; } = 100.0; [Display("移动速度 (mm/s)", Order: 3)]
        public double Velocity { get; set; } = 20.0;

        public AxisMoveStep() { Rules.Add(() => Axis != null, "必须指定一个轴", "Axis"); }

        public override void Run()
        {
            Log.Info($"命令轴移动至 {TargetPos} mm, 速度 {Velocity} mm/s ...");

            // 这一句会卡住，直到物理运动完全结束！
            double finalPos = Axis.MoveTo(TargetPos, Velocity);

            Log.Info($"轴移动完成，最终物理反馈位置: {finalPos} mm");
            UpgradeVerdict(Verdict.Pass);
        }
    }
}