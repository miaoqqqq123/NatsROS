using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.BehaviorTree
{
    public class BehaviorTreePlugin : IDashboardPlugin
    {
        public string RibbonCategory => "全网监控 (Monitoring)";
        public string DisplayName => "AI 行为树监控 (BT Visualizer)";
        public string GlyphPath => "SvgImages/DiagramIcons/OrgChart.svg"; // 组织架构图图标

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BehaviorTreeView(nats);
        }
    }
}