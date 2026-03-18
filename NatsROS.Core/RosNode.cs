using MessagePack;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Logging;
using NatsROS.Core.Parameters;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core;


/// <summary>
/// NatsROS 节点基类
/// </summary>
public abstract class RosNode
{
    public string Name { get; }
    protected INatsClient Nats { get; }

    protected ILogger Logger { get; }

    public RosParameterServer Parameters { get; }

    // 用于管理节点生命周期的 Token
    private readonly CancellationTokenSource _nodeCts = new();

    public RosNode(INatsClient nats, string nodeName, ILogger? logger = null)
    {
        Nats = nats;
        Name = nodeName;

        if (logger == null)
        {
            var provider = new RosOutLoggerProvider(nats);
            Logger = provider.CreateLogger(nodeName);
        }
        else
        {
            Logger = logger;
        }

        // 初始化并启动参数服务器
        Parameters = new RosParameterServer(nats, nodeName);
        Parameters.Start(_nodeCts.Token);

        // 节点创建时，自动启动发现机制监听器 (后台运行)
        _ = StartDiscoveryListenerAsync(_nodeCts.Token);
    }

    // 创建一个客户端，去遥控别的节点的参数
    public RosParameterClient CreateParameterClient(string targetNodeName) => new(Nats, targetNodeName);


    private async Task StartDiscoveryListenerAsync(CancellationToken ct)
    {
        // 监听全局的节点发现 Ping 请求
        await foreach (var msg in Nats.SubscribeAsync<PingReq>("natsros.discovery.ping", cancellationToken: ct))
        {
            if (msg.Data is not null)
            {
                // 收到 Ping，回复本节点的信息
                await msg.ReplyAsync(new NodeInfo(Name), cancellationToken: ct);
            }
        }
    }

    public RosPublisher<T> CreatePublisher<T>(string topic, RosQosProfile? qos = null) where T : IRosMessage => new(Nats, topic, qos ?? RosQosProfile.SensorData);
    public RosSubscriber<T> CreateSubscriber<T>(string topic, RosQosProfile? qos = null) where T : IRosMessage => new(Nats, topic, qos ?? RosQosProfile.SensorData);
    public RosServiceClient<TReq, TRes> CreateClient<TReq, TRes>(string serviceName) where TReq : IRosMessage where TRes : IRosMessage => new(Nats, serviceName);
    public RosServiceServer<TReq, TRes> CreateServer<TReq, TRes>(string serviceName) where TReq : IRosMessage where TRes : IRosMessage => new(Nats, serviceName);

    public RosActionClient<TGoal, TFeedback, TResult> CreateActionClient<TGoal, TFeedback, TResult>(string actionName)
        where TGoal : IRosMessage where TFeedback : IRosMessage where TResult : IRosMessage
        => new(Nats, actionName);

    public RosActionServer<TGoal, TFeedback, TResult> CreateActionServer<TGoal, TFeedback, TResult>(string actionName)
        where TGoal : IRosMessage where TFeedback : IRosMessage where TResult : IRosMessage
        => new(Nats, actionName);

    // 提供一个优雅停机的方法
    public void Shutdown() => _nodeCts.Cancel();
}

