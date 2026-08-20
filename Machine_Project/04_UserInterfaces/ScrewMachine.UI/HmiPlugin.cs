using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NatsROS.Core.UI;
using System;

namespace ScrewMachine.UI
{
    // 这就是我们的 UI 插件！
    public class HmiPlugin : IHmiPlugin
    {
        public string DisplayName => "主操作台";
        public int OrderIndex => 10;

        public object CreateView(IServiceProvider serviceProvider)
        {
            // 从 DI 容器中拿到 NATS 客户端，注入给我们的 UI 控件
            var nats = serviceProvider.GetRequiredService<INatsClient>();
            return new DispenserConsole(nats);
        }
    }
}