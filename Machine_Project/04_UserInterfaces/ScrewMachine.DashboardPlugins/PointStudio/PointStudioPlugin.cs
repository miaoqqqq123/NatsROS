using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace ScrewMachine.Dashboard.Plugins.PointStudio
{
    public class PointStudioPlugin : IDashboardPlugin
    {
        public string RibbonPage => "设备应用 (Device Apps)";
        public string RibbonGroup => "工艺与调试 (Process & Debug)";
        public string DisplayName => "在线点位示教器 (Teaching)";
        // 使用一个代表目标或准星的图标
        public string GlyphPath => "SvgImages/Icon Builder/Business_Target.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new PointStudioView(nats);
        }
    }
}