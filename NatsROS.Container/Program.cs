using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NatsROS.Container;
using NatsROS.Hosting;

namespace NatsROS.Container
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // 1. 创建现代化的后台主机构建器
            var builder = Host.CreateApplicationBuilder(args);

            // 【核心】：将其注册为 Windows 服务！(在 Linux 下可以无缝忽略，或者改为 Systemd)
            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "NatsROS_Container_Engine";
            });

            builder.Logging.SetMinimumLevel(LogLevel.Information);

            // 2. 注入 NatsROS 核心
            builder.AddNatsRos("nats://127.0.0.1:4222");

            // 3. 注册我们刚刚写的管家和母体节点
            builder.Services.AddSingleton<DynamicNodeManager>();
            builder.AddRosNode<ContainerManagerNode>();

            // 4. 构建并启动！
            var host = builder.Build();

            Console.WriteLine("======================================================");
            Console.WriteLine("   NatsROS Container Engine (无头母体) 初始化完成     ");
            Console.WriteLine("   等待 Dashboard/HMI 的远程 RPC 指令...              ");
            Console.WriteLine("======================================================");

            await host.RunAsync();
        }
    }
}
