using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.SecurityStudio
{
    public class SecurityStudioPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "系统核心 (System Core)";
        public string DisplayName => "身份与权限中心 (IAM)";
        public string GlyphPath => "SvgImages/Icon Builder/Security_Key.svg";

        public object CreateView(IServiceProvider serviceProvider) => new SecurityStudioView(serviceProvider.GetRequiredService<INatsClient>());
    }
}