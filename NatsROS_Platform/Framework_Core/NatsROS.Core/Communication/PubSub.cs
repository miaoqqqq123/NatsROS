// ==========================================
// 智能双引擎发布者
// ==========================================
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using NatsROS.Core;
using NatsROS.Core.Communication;
using System.Runtime.CompilerServices;

namespace NatsROS.Core.Communication;

public class RosPublisher<T>(INatsClient nats, string topicName, RosQosProfile qos) where T : IRosMessage
{
    private INatsJSContext? _jsContext;
    private bool _isReliable = qos.Reliability == RosReliability.Reliable;

    // 【核心魔法 1】：在初始化时，提取当前 T 的完整类名，做成 NATS 消息头！
    // typeof(T).FullName 拿到的就是 "NatsROS.Messages.StdMsgs.StringMsg" 这种字符串
    private readonly NatsHeaders _headers = new() 
    {
        { "ros-type", $"{typeof(T).FullName}, {typeof(T).Assembly.GetName().Name}" }
    };

    public async ValueTask PublishAsync(T message, CancellationToken cancellationToken = default)
    {
        if (_isReliable)
        {
            // 可靠传输：使用 JetStream 发送
            _jsContext ??= nats.CreateJetStreamContext();

            // 架构师注：在发送前，我们要确保网络上存在这个 Topic 的持久化流 (Stream)
            // 在工业级应用中，流通常由管理员提前建好。这里为了极简体验，我们做自动懒加载创建
            await EnsureStreamExistsAsync(_jsContext, topicName, qos.HistoryDepth, cancellationToken);

            await _jsContext.PublishAsync(topicName, message, headers: _headers, cancellationToken: cancellationToken);
        }
        else
        {
            // 尽力而为：使用 Core NATS 极速发送
            await nats.PublishAsync(topicName, message, headers: _headers, cancellationToken: cancellationToken);
        }
    }

    private static async ValueTask EnsureStreamExistsAsync(INatsJSContext js, string topic, int depth, CancellationToken ct)
    {
        var streamName = $"ROS_STREAM_{topic.Replace(".", "_")}";
        try
        {
            await js.CreateStreamAsync(new StreamConfig(streamName, [topic])
            {
                MaxMsgs = depth,               // 历史深度
                Retention = StreamConfigRetention.Limits // 按限制淘汰旧数据
            }, ct);
        }
        catch { /* 忽略流已存在的异常 */ }
    }
}

// ==========================================
// 智能双引擎订阅者
// ==========================================
public class RosSubscriber<T>(INatsClient nats, string topicName, RosQosProfile qos) where T : IRosMessage
{
    private readonly bool _isReliable = qos.Reliability == RosReliability.Reliable;

    public async IAsyncEnumerable<T> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_isReliable)
        {
            // 可靠传输：从 JetStream 消费者拉取 (支持断线续传和历史读取)
            var js = nats.CreateJetStreamContext();
            var streamName = $"ROS_STREAM_{topicName.Replace(".", "_")}";


            // 订阅端也必须承担确保流存在的责任！
            // 防止订阅者比发布者先启动导致 stream not found 异常
            // 为每一个订阅者实例生成全球唯一的消费者名字！
            // 彻底解决多窗口订阅同名流时的“负载均衡/消息被偷”问题，实现 100% 全网广播。

            string consumerName = $"{streamName}_SUB_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            try
            {
                await js.CreateStreamAsync(new StreamConfig(streamName, new[] { topicName })
                {
                    MaxMsgs = qos.HistoryDepth,
                    Retention = StreamConfigRetention.Limits
                }, cancellationToken);
            }
            catch { /* 如果发布者已经建好流了，这里会报流已存在，我们静默忽略即可 */ }


            // 为当前订阅者创建一个独占的消费者，起点配置为：如果是 TransientLocal 则读取历史
            var deliverPolicy = qos.Durability == RosDurability.TransientLocal
                ? ConsumerConfigDeliverPolicy.All
                : ConsumerConfigDeliverPolicy.New;

            // 传入唯一名字，并设置 InactiveThreshold！
            // 因为 UI 窗口可能会被频繁关闭/重开，设置 1 分钟不活动则自动清理，防止 NATS 服务器内存泄漏！
            var consumerConfig = new ConsumerConfig(consumerName)
            {
                DeliverPolicy = deliverPolicy,
                AckPolicy = ConsumerConfigAckPolicy.None,
                InactiveThreshold = TimeSpan.FromMinutes(1) // 1分钟没连上，NATS自动销毁这个临时消费者
            };

            var consumer = await js.CreateOrUpdateConsumerAsync(streamName, consumerConfig, cancellationToken);

            await foreach (var msg in consumer.ConsumeAsync<T>(cancellationToken: cancellationToken))
            {
                if (msg.Data is not null) yield return msg.Data;
            }
        }
        else
        {
            // 尽力而为：使用 Core NATS 极速监听
            await foreach (var msg in nats.SubscribeAsync<T>(topicName, cancellationToken: cancellationToken))
            {
                if (msg.Data is not null) yield return msg.Data;
            }
        }
    }
}

