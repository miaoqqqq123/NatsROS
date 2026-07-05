using MessagePack;
using NatsROS.Core;
using System.Collections.Generic;

// 【极其关键】：告诉大管家，我这个 DLL 里面有报警定义！
// 注意：assembly 标签必须写在 namespace 之外！
[assembly: NatsROS.Core.Attributes.ContainsNatsRosAlarms]

namespace NatsROS.Messages.AEM;

/// <summary>
/// 定义一个打在常量上的特性标签，用于提供“出厂默认值”
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class AlarmDefaultAttribute : Attribute
{
    public AlarmLevel Level { get; }
    public string Template { get; }
    public bool IsLatching { get; }
    public bool AllowBypass { get; }

    public AlarmDefaultAttribute(AlarmLevel level, string template, bool isLatching = false, bool allowBypass = false)
    {
        Level = level;
        Template = template;
        IsLatching = isLatching;
        AllowBypass = allowBypass;
    }
}

/// <summary>
/// 强类型的全局报警字典！程序员以后只用这个类！
/// </summary>
public static class AlarmKeys
{
    [AlarmDefault(AlarmLevel.Warning, "相机未找到定位点。参数:[{0}]", isLatching: false)]
    public const string VSN_MARK_NOT_FOUND = "ERR_VSN_001";

    [AlarmDefault(AlarmLevel.Critical, "伺服轴发生过载或急停！参数:[{0}]", isLatching: true)]
    public const string MOT_AXIS_OVERLOAD = "ERR_MOT_001";

    [AlarmDefault(AlarmLevel.Warning, "系统气压不足。当前值:[{0}]", isLatching: true, allowBypass: true)]
    public const string SYS_LOW_AIR_PRESSURE = "ERR_AIR_001";

    // 💡 以后如果需要新增报警，只要在这里加一行代码即可，剩下的全交给系统自动处理！
}

public enum AlarmLevel : byte 
{ 
    Info = 10, 
    Warning = 20, 
    Critical = 30 
}

public enum AlarmStatus : byte
{ 
    Raised = 0, 
    Acknowledged = 1,
    Cleared = 2 
}

// ==========================================
// 1. 数据驱动的字典定义 (对应 alarms.json)
// ==========================================
public record AlarmDefinition(
    string Code,
    AlarmLevel Level,
    string Template,
    bool IsLatching,
    bool AllowBypass);

// ==========================================
// 2. 节点抛出与消除的极简报文 (Pub/Sub)
// 底层节点绝对不传长文本，只传 Code 和 参数！
// ==========================================
[MessagePackObject]
public record RaiseAlarmMsg(
    [property: Key(0)] string Code,
    [property: Key(1)] string[] Args
) : IRosMessage;

[MessagePackObject]
public record ClearAlarmMsg(
    [property: Key(0)] string Code
) : IRosMessage;

// ==========================================
// 3. 供 HMI 大屏展示与交互的完整活动报警对象
// ==========================================
[MessagePackObject]
public record ActiveAlarmState(
    [property: Key(0)] string Code,
    [property: Key(1)] AlarmLevel Level,
    [property: Key(2)] string FormattedMessage,
    [property: Key(3)] AlarmStatus Status,
    [property: Key(4)] long FirstRaisedTime,
    [property: Key(5)] long LastRaisedTime,
    [property: Key(6)] int Occurrences, // 发生次数（用于防风暴统计）
    [property: Key(7)] bool IsLatching
) : IRosMessage;

// ==========================================
// 4. HMI 与 Manager 的交互 RPC (确认/同步)
// ==========================================
[MessagePackObject]
public record AckAlarmReq(
    [property: Key(0)] string Code
) : IRosRequest<AckAlarmRes>; 

[MessagePackObject]
public record AckAlarmRes(
    [property: Key(0)] bool Success
) : IRosMessage;

// 供 HMI 刚开机时拉取全量活动报警
[MessagePackObject]
public record SyncAlarmsReq() : IRosRequest<SyncAlarmsRes>; 

[MessagePackObject]
public record SyncAlarmsRes(
    [property: Key(0)] List<ActiveAlarmState> ActiveAlarms
) : IRosMessage;

// 当报警池发生变化时，Manager 会主动广播此消息通知所有 HMI 刷新界面
[MessagePackObject]
public record AlarmsChangedEvent(
    [property: Key(0)] List<ActiveAlarmState> ActiveAlarms
) : IRosMessage;