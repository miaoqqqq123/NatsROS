using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.Watchdog
{
    public class WatchdogPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "全网诊断 (Diagnostics)";
        public string DisplayName => "APM 性能看门狗 (Watchdog)";
        public string GlyphPath => "SvgImages/Dashboards/Gauges.svg";

        public object CreateView(IServiceProvider serviceProvider) => new WatchdogView(serviceProvider.GetRequiredService<INatsClient>());
    }
}