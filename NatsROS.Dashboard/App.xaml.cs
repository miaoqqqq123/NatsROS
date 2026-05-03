using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using Application = System.Windows.Application;

namespace NatsROS.Dashboard
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    { // 全局单例的 DI 容器提供者
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();

            // 1. 注册核心 NATS 客户端 (单例)
            services.AddSingleton<INatsClient>(_ =>
            {
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                var client = new NatsClient(options);
                client.ConnectAsync().GetAwaiter().GetResult(); // 启动时强制连接
                return client;
            });

            // 如果有其他的全局服务 (比如配置读取、日志管理)，都在这里注册...

            ServiceProvider = services.BuildServiceProvider();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            // 优雅关闭 NATS 连接
            var nats = ServiceProvider.GetService<INatsClient>();
            if (nats != null) await nats.DisposeAsync();

            base.OnExit(e);
        }
    }

}
