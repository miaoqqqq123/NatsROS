using MessagePack;
using NatsROS.Core;

namespace ScrewMachine.Messages.Hardware;

// ==========================================
// 1. IO 置位请求 (如：开启气缸、点亮指示灯)
// ==========================================
[MessagePackObject]
public record SetIoReq(
    [property: Key(0)] int Pin,
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
    [property: Key(0)] int Pin
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
    [property: Key(0)] int Pin,
    [property: Key(1)] bool State
) : IRosMessage;