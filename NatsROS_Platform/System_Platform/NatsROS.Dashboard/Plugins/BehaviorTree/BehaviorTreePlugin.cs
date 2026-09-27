using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.BehaviorTree
{
    public class BehaviorTreePlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "操作与监控 (Operation)";
        public string DisplayName => "行为树监控 (BT Visualizer)";
        public string GlyphPath => "SvgImages/DiagramIcons/TopDown.svg"; // 组织架构图图标
        public bool AllowMultiple => true; // 允许无限多开！

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BehaviorTreeView(nats);
        }
    }
}