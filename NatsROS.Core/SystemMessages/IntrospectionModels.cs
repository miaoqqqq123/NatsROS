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


// ==========================================
// 行为树 (Behavior Tree) 遥测契约
// ==========================================
public enum BtNodeStatus : byte { Idle = 0, Running = 1, Success = 2, Failure = 3 }
public enum BtNodeType : byte { Sequence = 0, Selector = 1, Action = 2, Condition = 3 }

[MessagePackObject]
public record BtNodeDef(
    [property: Key(0)] string Id, 
    [property: Key(1)] string Name,
    [property: Key(2)] BtNodeType Type,
    [property: Key(3)] string[] ChildrenIds
) : IRosMessage; 

[MessagePackObject]
public record BtTopologyMsg(
    [property: Key(0)] string TreeName, 
    [property: Key(1)] BtNodeDef[] Nodes
) : IRosMessage;

[MessagePackObject]
public record BtStateMsg([property: Key(0)] string TreeName,
    [property: Key(1)] Dictionary<string, BtNodeStatus> NodeStates
) : IRosMessage;


// 【新增】：向大脑索要行为树拓扑图的请求
[MessagePackObject]
public record BtTopologyReq() : IRosRequest<BtTopologyMsg>;