using MessagePack;
using NatsROS.Core;
using NatsROS.Messages.StdMsgs;
using NatsROS.Messages.GeometryMsgs;

namespace NatsROS.Messages.SensorMsgs;

/// <summary>
/// IMU (惯性测量单元) 数据
/// </summary>
[MessagePackObject]
public record Imu(
    [property: Key(0)] Header Header,
    [property: Key(1)] Quaternion Orientation,
    [property: Key(2)] Vector3 AngularVelocity,
    [property: Key(3)] Vector3 LinearAcceleration
) : IRosMessage;

/// <summary>
/// 环境温度
/// </summary>
[MessagePackObject]
public record Temperature(
    [property: Key(0)] Header Header,
    [property: Key(1)] double TemperatureCelsius,
    [property: Key(2)] double Variance
) : IRosMessage;
