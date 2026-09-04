using System;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;

namespace NatsROS.Dashboard.Plugins.NodeManager
{
    public class NodeManagerPlugin : IDashboardPlugin
    {
        public string RibbonPage => "系统核心 (System Core)";
        public string RibbonGroup => "全网诊断 (Diagnostics)";
        public string DisplayName => "母体节点大盘 (Node Manager)";
        public string GlyphPath => "SvgImages/Icon Builder/Security_Assistance.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new NodeManagerView(nats);
        }
    }
}
