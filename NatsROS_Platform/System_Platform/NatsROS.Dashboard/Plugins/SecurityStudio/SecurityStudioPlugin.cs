using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.SecurityStudio
{
    public class SecurityStudioPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "平台管家 (Platform Managers)";
        public string DisplayName => "身份与权限中心 (IAM)";
        public string GlyphPath => "SvgImages/Icon Builder/Security_Key.svg";

        public object CreateView(IServiceProvider serviceProvider) => new SecurityStudioView(serviceProvider.GetRequiredService<INatsClient>());
    }
}