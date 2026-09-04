using NatsROS.Core.UI; // 直接引用底层的核心 SDK
using System;
using NATS.Client.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NatsROS.Plugin.EchoTest
{
    public class EchoTestPlugin : IDashboardPlugin
    {
        public string RibbonPage => "调试应用 (Device Apps)";
        public string RibbonGroup => "调试功能 (Experimental)";
        public string DisplayName => "回音壁测试 (Echo Test)";
        public string GlyphPath => "SvgImages/Scheduling/GroupByResource.svg";

        public bool AllowMultiple => false;

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new EchoTestView(nats);
        }
    }
}