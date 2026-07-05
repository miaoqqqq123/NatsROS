using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.GeometryMsgs;

[MessagePackObject]
public record Vector3(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z
) : IRosMessage;

[MessagePackObject]
public record Quaternion(
    [property: Key(0)] double X,
    [property: Key(1)] double Y,
    [property: Key(2)] double Z,
    [property: Key(3)] double W
) : IRosMessage;

/// <summary>
/// Twist 是 ROS 中最著名的消息之一，用于控制机器人的底盘移动（线速度和角速度）
/// </summary>
[MessagePackObject]
public record Twist(
    [property: Key(0)] Vector3 Linear,  // 线速度 (m/s)
    [property: Key(1)] Vector3 Angular  // 角速度 (rad/s)
) : IRosMessage;
