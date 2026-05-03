using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.BehaviorTreeEditor
{
    public class BtEditorPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "系统核心 (System Core)";
        public string DisplayName => "行为树编辑器 (BT Studio)";
        // 使用一个代表流程或结构的图标
        public string GlyphPath => "SvgImages/DiagramIcons/ShapeArrowVertical.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BtEditorView(nats);
        }
    }
}