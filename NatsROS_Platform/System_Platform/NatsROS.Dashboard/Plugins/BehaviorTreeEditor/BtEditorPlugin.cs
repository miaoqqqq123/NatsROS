using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.BehaviorTreeEditor
{
    public class BtEditorPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "行为树编辑器 (BT Studio)";
        // 使用一个代表流程或结构的图标
        public string GlyphPath => "SvgImages/DiagramIcons/ReLayoutParts.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BtEditorView(nats);
        }
    }
}