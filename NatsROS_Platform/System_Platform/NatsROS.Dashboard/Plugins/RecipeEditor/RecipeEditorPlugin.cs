using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.RecipeEditor
{
    public class RecipeEditorPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "系统核心 (System Core)";
        public string DisplayName => "开机配方编排器 (Launch Recipe)";
        public string GlyphPath => "SvgImages/Dashboards/Cards.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new RecipeEditorView(nats);
        }
    }
}
