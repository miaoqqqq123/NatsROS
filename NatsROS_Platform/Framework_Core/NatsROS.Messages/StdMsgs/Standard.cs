using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.StdMsgs;

/// <summary>
/// 几乎所有传感器数据都会携带的报头，用于时间同步和坐标系转换
/// </summary>
[MessagePackObject]
public record Header(
    [property: Key(0)] long Stamp,    // 时间戳 (如 DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    [property: Key(1)] string FrameId // 坐标系 ID (如 "base_link", "laser_frame")
) : IRosMessage;

[MessagePackObject]
public record StringMsg([property: Key(0)] string Data) : IRosMessage;

[MessagePackObject]
public record Int32Msg([property: Key(0)] int Data) : IRosMessage;

[MessagePackObject]
public record Float64Msg([property: Key(0)] double Data) : IRosMessage;

// 定义 Service 的请求和响应模型 (例如：请求计算两数之和)
[MessagePackObject]
public record AddTwoIntsReq([property: Key(0)] int A, [property: Key(1)] int B) : IRosMessage;

[MessagePackObject]
public record AddTwoIntsRes([property: Key(0)] int Sum) : IRosMessage;

