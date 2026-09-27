using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace ScrewMachine.Dashboard.Plugins.HardwareStudio
{
    public class HardwareStudioPlugin : IDashboardPlugin
    {
        public string RibbonPage => "机台专区 (Machine)";
        public string RibbonGroup => "实施与标定 (Commissioning)";
        public string DisplayName => "机电硬件标定台 (Hardware Studio)";
        // 使用一个扳手或设置图标
        public string GlyphPath => "SvgImages/Chart/ChartType_PolarRangeArea.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new HardwareStudioView(nats);
        }
    }
}