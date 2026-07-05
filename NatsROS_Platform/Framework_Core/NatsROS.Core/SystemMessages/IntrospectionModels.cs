using MessagePack;

namespace NatsROS.Core.SystemMessages;

// ==========================================
// 全网节点自省 (Introspection) 请求与响应
// ==========================================
[MessagePackObject]
public record NodeIntrospectionReq(
    [property: Key(0)] string RequestId
    ) : IRosRequest<NodeIntrospectionRes>;

[MessagePackObject]
public record NodeIntrospectionRes(
    [property: Key(0)] string NodeName,
    [property: Key(1)] string[] Publishers,     // 该节点发布的所有话题
    [property: Key(2)] string[] Subscribers,    // 该节点订阅的所有话题
    [property: Key(3)] string[] ServiceServers, // 该节点提供的所有服务
    [property: Key(4)] string[] ServiceClients  // 该节点调用的所有服务
) : IRosMessage;

