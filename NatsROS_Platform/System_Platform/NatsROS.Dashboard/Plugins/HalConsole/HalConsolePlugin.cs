using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.HalConsole
{
    public class HalConsolePlugin : IDashboardPlugin
    {
        public string RibbonCategory => "开发与调试 (Dev Tools)";
        public string DisplayName => "L3 硬件调试台 (HAL Console)";
        public string GlyphPath => "SvgImages/Dashboards/EditRules.svg";

        public object CreateView(IServiceProvider serviceProvider) => new HalConsoleView(serviceProvider.GetRequiredService<INatsClient>());
    }
}