using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.Watchdog
{
    public class WatchdogPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "全网监控 (Monitoring)";
        public string DisplayName => "APM 性能看门狗 (Watchdog)";
        public string GlyphPath => "SvgImages/Dashboards/Gauges.svg";

        public object CreateView(IServiceProvider serviceProvider) => new WatchdogView(serviceProvider.GetRequiredService<INatsClient>());
    }
}