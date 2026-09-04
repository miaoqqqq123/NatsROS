using MessagePack;

namespace NatsROS.Core.SystemMessages;


// ==========================================
// 行为树 (Behavior Tree) 遥测契约
// ==========================================
public enum BtNodeStatus : byte
{
    Idle = 0,    // 未执行 / 已重置
    Running = 1, // 正在执行长任务 (异步)
    Success = 2, // 执行成功
    Failure = 3  // 执行失败
}

// 节点类型，专供 NatsROS Dashboard 画拓扑图使用
public enum BtNodeType : byte
{
    Root = 0,
    Sequence = 1,
    Selector = 2,
    Action = 3,
    Decorator = 4, // 菱形 (例如 Retry)
    Condition = 5
}

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

// ==========================================
// 行为树动态控制 (热重载与启停) 请求与响应
// ==========================================[MessagePackObject]
public record ReloadTreeReq(
    [property: Key(0)] string XmlContent
) : IRosRequest<ReloadTreeRes>;

[MessagePackObject]
public record ReloadTreeRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;



[MessagePackObject]
public record StartTreeReq(
    [property: Key(0)] Dictionary<string, string>? ContextData = null,
    [property: Key(1)] bool IsLoop = false  
) : IRosRequest<StartTreeRes>;

[MessagePackObject]
public record StartTreeRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;


[MessagePackObject]
public record StopTreeReq() : IRosRequest<StopTreeRes>;

[MessagePackObject]
public record StopTreeRes(
    [property: Key(0)] bool Success, [property: Key(1)] string Message
) : IRosMessage;

// 【新增】：大脑执行完整个工艺树后，向全网广播的最终判定结果
[MessagePackObject]
public record BtResultMsg(
    [property: Key(0)] bool IsSuccess
) : IRosMessage;

// ==========================================
// 行为树 暂停 / 继续 控制契约
// ==========================================
[MessagePackObject]
public record PauseTreeReq() : IRosRequest<PauseTreeRes>;

[MessagePackObject]
public record PauseTreeRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;

[MessagePackObject]
public record ResumeTreeReq() : IRosRequest<ResumeTreeRes>;

[MessagePackObject]
public record ResumeTreeRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
) : IRosMessage;