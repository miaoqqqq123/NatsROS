using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace ScrewMachine.Dashboard.Plugins.DigitalTwin
{
    public class DigitalTwinPlugin : IDashboardPlugin
    {
        public string RibbonPage => "设备应用 (Device Apps)";
        public string RibbonGroup => "操作与监控 (Operation)";
        public string DisplayName => "3D 数字孪生室 (Digital Twin)";
        public string GlyphPath => "SvgImages/DiagramIcons/Orientation/ListOrientation.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new DigitalTwinView(nats);
        }
    }
}