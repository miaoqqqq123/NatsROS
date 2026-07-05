using MessagePack;

namespace NatsROS.Core.SystemMessages;

public static class RosLogLevels
{
    public const byte Debug = 10;
    public const byte Info = 20;
    public const byte Warn = 30;
    public const byte Error = 40;
    public const byte Fatal = 50;
}

[MessagePackObject]
public record LogMsg(
    [property: Key(0)] long Stamp,
    [property: Key(1)] byte Level,
    [property: Key(2)] string Name,
    [property: Key(3)] string Msg
) : IRosMessage;

// 1. 定义发现协议的数据模型
[MessagePackObject]
public record NodeInfo([property: Key(0)] string NodeName) : IRosMessage;

[MessagePackObject]
public record PingReq([property: Key(0)] string RequestId) : IRosMessage;



/// <summary>
/// 工程与工作区热切换事件
/// </summary>
/// <param name="NewProjectName"></param>
/// <param name="TimestampTick"></param>

[MessagePackObject]
public record WorkspaceChangedEvent(
    [property: Key(0)] string NewProjectName,      // 切换到了哪个新工程
    [property: Key(1)] long TimestampTick          // 切换发生的时间戳
) : IRosMessage;
