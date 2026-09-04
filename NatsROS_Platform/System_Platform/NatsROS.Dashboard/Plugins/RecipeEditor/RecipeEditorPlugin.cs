using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.RecipeEditor
{
    public class RecipeEditorPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "开机配方编排器 (Launch Recipe)";
        public string GlyphPath => "SvgImages/Dashboards/Cards.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new RecipeEditorView(nats);
        }
    }
}
