using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;
using NatsROS.Plugin.EchoTest;
using System;
using System.Windows.Controls;

namespace HexivMachine.Dashboard.Plugins
{
    public class SamplePlugin : IDashboardPlugin
    {
        public string RibbonPage => "机台专区 (Machine)";
        public string RibbonGroup => "示例工具";
        public string DisplayName => "模板示例控制台";
        public string GlyphPath => "SvgImages/Scheduling/GroupByResource.svg";

        public object CreateView(IServiceProvider serviceProvider)
        {
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new EchoTestView(nats);
        }
    }
}