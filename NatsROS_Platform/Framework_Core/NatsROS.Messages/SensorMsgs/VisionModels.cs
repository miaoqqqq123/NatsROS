using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.SensorMsgs;

// ==========================================
// 视觉定位 RPC 契约
// ==========================================
[MessagePackObject]
public record FindMarkReq() : IRosRequest<FindMarkRes>;

[MessagePackObject]
public record FindMarkRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] double OffsetX,     // X轴物理偏差 (mm)
    [property: Key(2)] double OffsetY,     // Y轴物理偏差 (mm)
    [property: Key(3)] double AngleDegree  // 旋转角度 (度)
) : IRosMessage;