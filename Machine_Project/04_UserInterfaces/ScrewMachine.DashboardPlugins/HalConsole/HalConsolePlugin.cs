using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace ScrewMachine.Dashboard.Plugins.HalConsole
{
    public class HalConsolePlugin : IDashboardPlugin
    {
        public string RibbonPage => "设备应用 (Device Apps)";
        public string RibbonGroup => "工艺与调试 (Process & Debug)";
        public string DisplayName => "L3 硬件调试台 (HAL Console)";
        public string GlyphPath => "SvgImages/Dashboards/EditRules.svg";

        public object CreateView(IServiceProvider serviceProvider) => new HalConsoleView(serviceProvider.GetRequiredService<INatsClient>());
    }
}