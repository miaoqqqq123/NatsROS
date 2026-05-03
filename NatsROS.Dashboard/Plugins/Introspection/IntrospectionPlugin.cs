using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.Introspection
{
    public class IntrospectionPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "全网监控 (Monitoring)";
        public string DisplayName => "节点拓扑图 (Node Graph)";
        // DevExpress 内置的一个网状图标
        public string GlyphPath => "SvgImages/DiagramIcons/GenerateData.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new NodeGraphView(nats);
        }
    }
}
