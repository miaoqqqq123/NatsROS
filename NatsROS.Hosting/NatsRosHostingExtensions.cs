using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Logging;
using NatsROS.Core.Serialization;

namespace NatsROS.Hosting;

public static class NatsRosHostingExtensions
{
    /// <summary>
    /// 将 NatsROS 核心引擎注入到 .NET 容器中
    /// </summary>
    public static IHostApplicationBuilder AddNatsRos(this IHostApplicationBuilder builder, string natsUrl = "nats://127.0.0.1:4222")
    {
        // 1. 注册全局单例的 NATS 客户端
        builder.Services.AddSingleton<INatsClient>(_ =>
        {
            var options = NatsOpts.Default with
            {
                Url = natsUrl,
                SerializerRegistry = new NatsRosSerializerRegistry()
            };
            return new NatsClient(options);
        });

        // 2. 注入我们之前在 Core 里写的 /rosout 拦截器！
        // 这意味着，以后代码中任何通过 ILogger<T> 打印的日志，都会自动飞向 NATS 网络
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, RosOutLoggerProvider>());

        return builder;
    }

    /// <summary>
    /// 将开发者编写的业务节点注册为标准的后台服务
    /// </summary>
    public static IHostApplicationBuilder AddRosNode<TNode>(this IHostApplicationBuilder builder) where TNode : HostedRosNode
    {
        // 注册节点自身 (方便其他系统组件通过 DI 获取它的实例)
        builder.Services.AddSingleton<TNode>();

        // 将其桥接到 IHostedService，受宿主生命周期管控
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TNode>());

        return builder;
    }
}
