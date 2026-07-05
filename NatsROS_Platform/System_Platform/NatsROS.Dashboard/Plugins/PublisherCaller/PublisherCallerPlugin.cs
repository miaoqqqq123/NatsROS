using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.PublisherCaller
{
    public class PublisherCallerPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "开发与调试 (Dev Tools)";
        public string DisplayName => "消息发布与服务调试 (Pub/Call)";
        public string GlyphPath => "SvgImages/Icon Builder/Actions_Send.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new PublisherCallerView(nats);
        }
    }
}
