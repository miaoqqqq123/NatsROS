using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Hosting;
using NatsROS.Messages.StdMsgs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Examples
{
    // ==========================================
    // 支持被动态加载的发布者节点
    // ==========================================
    public class DynamicTalkerNode(INatsClient nats, string nodeName, ILogger<DynamicTalkerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("✅ 动态节点 [{NodeName}] 已在母体内激活！", Name);

            var publisher = CreatePublisher<StringMsg>("chatter");
            int count = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                var msg = new StringMsg($"我是 {Name}，发送第 {count++} 帧数据");
                Logger.LogDebug("发布: {Data}", msg.Data);

                await publisher.PublishAsync(msg, stoppingToken);
                await Task.Delay(100, stoppingToken); // 2秒发一次
            }
        }
    }

    // ==========================================
    // 支持被动态加载的订阅者节点
    // ==========================================
    public class DynamicListenerNode(INatsClient nats, string nodeName, ILogger<DynamicListenerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogWarning("👂 监听节点 [{NodeName}] 开始窃听 chatter 话题...", Name);

            var subscriber = CreateSubscriber<StringMsg>("chatter");

            await foreach (var msg in subscriber.SubscribeAsync(stoppingToken))
            {
                Logger.LogInformation("收到数据 -> {Data}", msg.Data);
            }
        }
    }

    // 为了让项目能被单独编译成 EXE 或 DLL，保留一个空的 Main
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("这个项目现在主要作为类库 (DLL) 供 NatsROS.Container 动态加载。");
            Console.WriteLine("请启动 Container 项目，并使用 CLI 命令加载这里的节点。");
        }
    }
}
