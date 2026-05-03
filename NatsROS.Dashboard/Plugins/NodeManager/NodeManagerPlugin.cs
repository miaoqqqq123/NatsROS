using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.NodeManager
{
    public class NodeManagerPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "系统核心 (System Core)";
        public string DisplayName => "母体节点大盘 (Node Manager)";
        public string GlyphPath => "SvgImages/Icon Builder/Security_Hardware.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new NodeManagerView(nats);
        }
    }
}
