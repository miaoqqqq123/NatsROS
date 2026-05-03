using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Dashboard.Infrastructure;

namespace NatsROS.Dashboard.Plugins.BagPlayer
{
    public class BagPlayerPlugin : IDashboardPlugin
    {
        public string RibbonCategory => "开发与调试 (Dev Tools)";
        public string DisplayName => "数据黑匣子 (Bag Player)";
        public string GlyphPath => "SvgImages/Icon Builder/Security_Video.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BagPlayerView(nats);
        }
    }
}
