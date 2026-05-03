using MessagePack;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Lifecycle;
using NatsROS.Core.Logging;
using NatsROS.Core.Parameters;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Concurrent;
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

    private readonly ConcurrentBag<string> _publishers = new();
    private readonly ConcurrentBag<string> _subscribers = new();
    private readonly ConcurrentBag<string> _srvServers = new();
    private readonly ConcurrentBag<string> _srvClients = new();

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

        // 【新增】：启动自省汇报监听！
        _ = StartIntrospectionListenerAsync(_nodeCts.Token);
    }

    // 【新增】：当收到全网 "natsros.introspection.ping" 时，交出记账本
    private async Task StartIntrospectionListenerAsync(CancellationToken ct)
    {
        await foreach (var msg in Nats.SubscribeAsync<NodeIntrospectionReq>("natsros.introspection.ping", cancellationToken: ct))
        {
            if (msg.Data != null)
            {
                var res = new NodeIntrospectionRes(
                    Name,
                    _publishers.Distinct().ToArray(),
                    _subscribers.Distinct().ToArray(),
                    _srvServers.Distinct().ToArray(),
                    _srvClients.Distinct().ToArray()
                );
                await msg.ReplyAsync(res, cancellationToken: ct);
            }
        }
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

    /// <summary>
    /// 【修改】：在现有的工厂方法中，把名字写进记账本！
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="topic"></param>
    /// <param name="qos"></param>
    /// <returns></returns>
    public RosPublisher<T> CreatePublisher<T>(string topic, RosQosProfile? qos = null) where T : IRosMessage
    {
        _publishers.Add(topic);
        return new(Nats, topic, qos ?? RosQosProfile.SensorData);
    }

    public RosSubscriber<T> CreateSubscriber<T>(string topic, RosQosProfile? qos = null) where T : IRosMessage
    {
        _subscribers.Add(topic);
        return new(Nats, topic, qos ?? RosQosProfile.SensorData);
    }

    public RosServiceClient<TReq, TRes> CreateClient<TReq, TRes>(string serviceName) 
        where TReq : IRosRequest<TRes>
        where TRes : IRosMessage
    {
        _srvClients.Add(serviceName);
        return new(Nats, serviceName);
    }

    public RosServiceServer<TReq, TRes> CreateServer<TReq, TRes>(string serviceName) 
        where TReq : IRosRequest<TRes>
        where TRes : IRosMessage
    {
        _srvServers.Add(serviceName);
        return new(Nats, serviceName);
    }

    public RosActionClient<TGoal, TFeedback, TResult> CreateActionClient<TGoal, TFeedback, TResult>(string actionName)
        where TGoal : IRosActionGoal<TFeedback, TResult>
        where TFeedback : IRosMessage 
        where TResult : IRosMessage
    {
        // Action Client 实际上包含一个 Goal 发送(Client)，一个 Cancel 发送(Pub)，一个 Feedback 接收(Sub)
        _srvClients.Add($"{actionName}.goal");
        _publishers.Add($"{actionName}.cancel");
        _subscribers.Add($"{actionName}.feedback");
        return new(Nats, actionName);
    }

    public RosActionServer<TGoal, TFeedback, TResult> CreateActionServer<TGoal, TFeedback, TResult>(string actionName)
        where TGoal : IRosActionGoal<TFeedback, TResult>
        where TFeedback : IRosMessage 
        where TResult : IRosMessage
    {
        // Action Server 包含一个 Goal 接收(Server)，一个 Cancel 接收(Sub)，一个 Feedback 发送(Pub)
        _srvServers.Add($"{actionName}.goal");
        _subscribers.Add($"{actionName}.cancel");
        _publishers.Add($"{actionName}.feedback");
        return new(Nats, actionName);
    }

    // 提供一个优雅停机的方法
    public void Shutdown() => _nodeCts.Cancel();


    /// <summary>
    /// 生命周期与自愈属性
    /// </summary>
    public NodeLifecycleState CurrentState { get; protected set; } = NodeLifecycleState.Unconfigured;

    /// <summary>
    /// 默认不重启，把生杀大权交给用户配置
    /// </summary>
    public NodeRecoveryOptions RecoveryOptions { get; set; } = new NodeRecoveryOptions();

    /// <summary>
    /// 当节点状态发生改变时，触发此事件（母体 Container 将监听这个事件！）
    /// </summary>
    public event Action<string, NodeLifecycleState>? OnStateChanged;

    /// <summary>
    /// 供子类或外部框架切换节点状态，并自动触发通知
    /// </summary>
    public void ChangeState(NodeLifecycleState newState)
    {
        if (CurrentState == newState) return;

        CurrentState = newState;
        Logger.LogInformation("🔄 节点[{NodeName}] 状态变更为: {State}", Name, newState);

        // 触发事件，通知外层母体
        OnStateChanged?.Invoke(Name, newState);
    }
}

