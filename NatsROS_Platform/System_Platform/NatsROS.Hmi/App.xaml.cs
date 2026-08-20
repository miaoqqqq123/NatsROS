using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Serialization;
using System.Windows;
using Application = System.Windows.Application;

namespace NatsROS.Hmi
{
    public partial class App : Application
    {
        // 全局公开的 DI 容器，供插件加载时获取 NATS 等服务
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. 注册核心服务到 DI 容器
            var services = new ServiceCollection();
            services.AddSingleton<INatsClient>(_ =>
            {
                var options = NatsOpts.Default with { SerializerRegistry = new NatsRosSerializerRegistry() };
                var client = new NatsClient(options);
                client.ConnectAsync().GetAwaiter().GetResult();
                return client;
            });

            ServiceProvider = services.BuildServiceProvider();

            base.OnStartup(e);

            // 2. 显式启动主窗口
            var mainWindow = new MainWindow();
            this.MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            // 优雅关闭全局通信连接
            var nats = ServiceProvider?.GetService<INatsClient>();
            if (nats != null) await nats.DisposeAsync();

            base.OnExit(e);
        }
    }
}