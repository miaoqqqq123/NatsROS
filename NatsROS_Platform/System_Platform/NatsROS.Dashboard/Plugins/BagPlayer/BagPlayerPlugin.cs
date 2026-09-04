using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.BagPlayer
{
    public class BagPlayerPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "全网诊断 (Diagnostics)";
        public string DisplayName => "数据黑匣子 (Bag Player)";
        public string GlyphPath => "SvgImages/HybridDemoIcons/Tiles/HybridDemo_Deferred.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new BagPlayerView(nats);
        }
    }
}
