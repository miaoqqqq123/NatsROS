using MessagePack;

namespace NatsROS.Core.SystemMessages;

// ==========================================
// 1. 动态加载节点 请求与响应
// ==========================================
[MessagePackObject]
public record LoadNodeReq(
    [property: Key(0)] string AssemblyName, // 例如: "NatsROS.Examples"
    [property: Key(1)] string TypeName,     // 例如: "NatsROS.Examples.MotorNode"
    [property: Key(2)] string NodeName      // 你想给它起的运行实例名，例如: "motor_1"
) : IRosMessage;

[MessagePackObject]
public record LoadNodeRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;

// ==========================================
// 2. 动态卸载节点 请求与响应
// ==========================================
[MessagePackObject]
public record UnloadNodeReq([property: Key(0)] string NodeName) : IRosMessage;

[MessagePackObject]
public record UnloadNodeRes([property: Key(0)] bool Success, [property: Key(1)] string Message) : IRosMessage;

// ==========================================
// 3. 查询当前母体内运行的所有节点
// ==========================================
[MessagePackObject]
public record ListNodesReq() : IRosMessage;

[MessagePackObject]
public record ListNodesRes([property: Key(0)] string[] NodeNames) : IRosMessage;
