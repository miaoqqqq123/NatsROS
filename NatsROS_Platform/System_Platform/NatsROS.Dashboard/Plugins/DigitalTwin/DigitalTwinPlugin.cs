using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.DigitalTwin
{
    public class DigitalTwinPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "全网监控 (Monitoring)";
        public string DisplayName => "3D 数字孪生室 (Digital Twin)";
        public string GlyphPath => "SvgImages/DiagramIcons/Orientation/ListOrientation.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new DigitalTwinView(nats);
        }
    }
}