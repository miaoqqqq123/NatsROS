using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.RecipeStudio
{
    public class RecipeStudioPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "配方研发工作室 (RMS)";
        public string GlyphPath => "SvgImages/Dashboards/Cards.svg"; // 使用卡片/配方图标

        public object CreateView(IServiceProvider serviceProvider) => new RecipeStudioView(serviceProvider.GetRequiredService<INatsClient>());
    }
}