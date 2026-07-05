using OpenTap;
using System;

namespace NatsROS.OpenTAP.Plugins
{
    [Display("测量并判定 5V 电压", Group: "NatsROS Steps", Description: "调用底层的 NatsROS 节点进行物理测量")]
    public class MeasureVoltageStep : TestStep
    {
        // 依赖注入我们刚才写的 Proxy 仪器[Display("目标万用表", Order: 1)]
        public NatsRosDmmInstrument Dmm { get; set; }
        [Display("电压上限 (V)", Order: 2)]
        public double HighLimit { get; set; } = 5.1;

        [Display("电压下限 (V)", Order: 3)]
        public double LowLimit { get; set; } = 4.9;

        public MeasureVoltageStep()
        {
            Rules.Add(() => Dmm != null, "必须选择一个 NatsROS 万用表代理！", "Dmm");
        }

        public override void Run()
        {
            Log.Info("正在向底层的 NatsROS Container 发送物理测量指令...");

            // 这里的调用会瞬间穿透 OpenTAP 进程，通过 NATS 打到底层节点上！
            double voltage = Dmm.MeasureVoltage();

            Log.Info($"收到底层物理节点传回的真实测量结果: {voltage:F4} V");

            // 使用 OpenTAP 自带的牛逼判定系统！
            bool isPass = voltage >= LowLimit && voltage <= HighLimit;

            // 记录测试结果并判定
            //Run.PublishResult("VoltageTest", this, new[] { "Measured_V", "LowerLimit", "UpperLimit" }, new object[] { voltage, LowLimit, HighLimit });
            UpgradeVerdict(isPass ? Verdict.Pass : Verdict.Fail);
        }
    }
}