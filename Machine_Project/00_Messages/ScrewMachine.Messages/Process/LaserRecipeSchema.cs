using NatsROS.Core.Attributes;
using NatsROS.Messages.RMS;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrewMachine.Messages.Process
{
    public enum GasType
    {
        [Description("氮气 (N2)")] Nitrogen = 0,
        [Description("氩气 (Ar)")] Argon = 1,
        [Description("压缩空气 (Air)")] CompressedAir = 2
    }

    // 【极其关键】：打上我们刚才定义的标签，方便大屏扫描！
    [RecipeSchema("激光切割机标准配方")]
    public class LaserRecipeSchema
    {
        [Category("1. 运动参数")]
        [DisplayName("平台移动速度 (mm/s)")]
        [DefaultValue(500.0)]
        public double TravelSpeed { get; set; } = 500.0;

        [Category("1. 运动参数")]
        [DisplayName("启用平滑插补")]
        [Description("勾选后拐角处不会减速停顿")]
        public bool EnableSmoothInterpolation { get; set; } = true;

        [Category("2. 工艺参数")]
        [DisplayName("保护气体类型")]
        public GasType ShieldingGas { get; set; } = GasType.Nitrogen;

        [Category("2. 工艺参数")]
        [DisplayName("激光功率 (%)")]
        public int LaserPowerPercent { get; set; } = 80;

        [Category("3. 轨迹与模型")]
        [DisplayName("CAD 轨迹文件路径")]
        [FilePath("DXF 图纸文件 (*.dxf)|*.dxf")] // 让大屏生成文件浏览按钮
        public string TrajectoryFilePath { get; set; } = "";

        // ==========================================
        // 【核心黑魔法：溢出数据收纳袋】
        // 作用：当反序列化 JSON 时，所有类里没定义的属性（比如动态添加的点位），都会被塞进这里。
        // 保存时，它又会把这些点位原封不动地吐回 JSON 根目录！保证动态点位绝对不丢失！
        // ==========================================
        [JsonExtensionData]
        [Browsable(false)] // 不要在界面(PropertyGrid)上显示它
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }
}