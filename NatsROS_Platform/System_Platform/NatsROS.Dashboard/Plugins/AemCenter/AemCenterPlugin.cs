using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.AemCenter
{
    public class AemCenterPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "报警与诊断中心 (AEM Center)";
        public string GlyphPath => "SvgImages/Business Objects/BO_Notifications.svg";

        public object CreateView(IServiceProvider serviceProvider)
            => new AemCenterView(serviceProvider.GetRequiredService<INatsClient>());
    }
}