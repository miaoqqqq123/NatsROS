using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.Introspection
{
    public class IntrospectionPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "全网诊断 (Diagnostics)";
        public string DisplayName => "节点拓扑图 (Node Graph)";
        // DevExpress 内置的一个网状图标
        public string GlyphPath => "SvgImages/Dashboards/DataLabels.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new NodeGraphView(nats);
        }
    }
}
