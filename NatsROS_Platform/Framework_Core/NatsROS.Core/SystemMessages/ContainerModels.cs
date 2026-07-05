using MessagePack;

namespace NatsROS.Core.SystemMessages;

// ==========================================
// 1. 动态加载节点 请求与响应
// ==========================================
[MessagePackObject]
public record LoadNodeReq(
    [property: Key(0)] string AssemblyName, // 例如: "NatsROS.Examples"
    [property: Key(1)] string TypeName,     // 例如: "NatsROS.Examples.MotorNode"
    [property: Key(2)] string NodeName,      // 你想给它起的运行实例名，例如: "motor_1"

    // 【新增】：带参启动与自愈策略！
    [property: Key(3)] Dictionary<string, string>? Parameters = null,
    [property: Key(4)] byte RestartPolicy = 0, // 0:Never, 1:OnFailure, 2:Always
    [property: Key(5)] int MaxRetries = 3,
    [property: Key(6)] int RestartDelaySeconds = 5

) : IRosRequest<LoadNodeRes>;

[MessagePackObject]
public record LoadNodeRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;

// ==========================================
// 2. 动态卸载节点 请求与响应
// ==========================================
[MessagePackObject]
public record UnloadNodeReq(
    [property: Key(0)] string NodeName
    ) : IRosRequest<UnloadNodeRes>;

[MessagePackObject]
public record UnloadNodeRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message
    ) : IRosMessage;

// ==========================================
// 3. 查询当前母体内运行的所有节点
// ==========================================
[MessagePackObject]
public record NodeStatusInfo(
    [property: Key(0)] string NodeName, 
    [property: Key(1)] byte State
    ) : IRosMessage;

[MessagePackObject]
public record ListNodesReq() : IRosRequest<ListNodesRes>;

[MessagePackObject]
public record ListNodesRes(
    [property: Key(0)] NodeStatusInfo[] Nodes
    ) : IRosMessage;

// ==========================================
// 4. 手动切换节点生命周期状态
// ==========================================
[MessagePackObject]
public record ChangeStateReq(
    [property: Key(0)] string NodeName, 
    [property: Key(1)] byte TargetState
    ) : IRosRequest<ChangeStateRes>; 

[MessagePackObject]
public record ChangeStateRes([property: Key(0)] bool Success) : IRosMessage;
