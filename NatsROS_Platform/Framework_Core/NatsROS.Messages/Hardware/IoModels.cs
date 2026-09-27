using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.Hardware;

// ==========================================
// 1. IO 置位请求 (如：开启气缸、点亮指示灯)
// ==========================================
[MessagePackObject]
public record SetIoReq(
    [property: Key(0)] string TagName,
    [property: Key(1)] bool State
) : IRosRequest<SetIoRes>;

[MessagePackObject]
public record SetIoRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message = ""
) : IRosMessage;

// ==========================================
// 2. IO 状态读取 (如：读取光电开关、磁性开关)
// ==========================================
[MessagePackObject]
public record GetIoReq(
    [property: Key(0)] string TagName
) : IRosRequest<GetIoRes>;

[MessagePackObject]
public record GetIoRes(
    [property: Key(0)] bool State,
    [property: Key(1)] bool Success
) : IRosMessage;

// ==========================================
// 3. IO 状态主动广播 (供 Dashboard 大屏做 IO 监控灯使用)
// ==========================================
[MessagePackObject]
public record IoStateChangedMsg(
    [property: Key(0)] string TagName, 
    [property: Key(1)] int PhysicalPin, 
    [property: Key(2)] bool State
) : IRosMessage;


// ==========================================
// 1. 强类型的 IO 点定义 (取代原来的 int)
// ==========================================
public enum IoType : byte { Input = 0, Output = 1 }

[MessagePackObject]
public record IoPointDefinition(
    [property: Key(0)] string TagName,
    [property: Key(1)] int PhysicalPin,
    [property: Key(2)] IoType Type,
    [property: Key(3)] string Description
) : IRosMessage;

// ==========================================
// 2. 索要 IO 字典的 RPC 契约 (大屏 -> 底层)
// ==========================================
[MessagePackObject]
public record GetIoDictReq() : IRosRequest<GetIoDictRes>;

[MessagePackObject]
public record GetIoDictRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] List<IoPointDefinition> IoPoints,
    [property: Key(2)] string Message = ""
) : IRosMessage;

// ==========================================
// 3. 保存 IO 字典的 RPC 契约 (大屏 -> 底层)
// ==========================================
[MessagePackObject]
public record SaveIoDictReq(
    [property: Key(0)] List<IoPointDefinition> IoPoints
) : IRosRequest<SaveIoDictRes>;

[MessagePackObject]
public record SaveIoDictRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message = ""
) : IRosMessage;