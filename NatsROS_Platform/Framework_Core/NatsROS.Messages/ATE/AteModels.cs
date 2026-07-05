using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.ATE;

// ==========================================
// 自动化测试：万用表电压测量 RPC 请求与响应
// ==========================================

[MessagePackObject]
public record MeasureVoltageReq() : IRosRequest<MeasureVoltageRes>;


[MessagePackObject]
public record MeasureVoltageRes(
    [property: Key(0)] double Voltage, 
    [property: Key(1)] bool IsSuccess,
    [property: Key(2)] string ErrorMessage = ""
) : IRosMessage;

// ==========================================
// 自动化测试引擎：流程执行 Action
// ==========================================

[MessagePackObject]
public record RunPlanGoal(
    [property: Key(0)] string Barcode = "",
    [property: Key(1)] string TaskId = ""
) : IRosActionGoal<RunPlanFeedback, RunPlanResult>;

[MessagePackObject]
public record RunPlanFeedback(
    [property: Key(0)] string StepName,
    [property: Key(1)] string Status, 
    [property: Key(2)] double ProgressPercentage
) : IRosMessage;

[MessagePackObject]
public record RunPlanResult([property: Key(0)] bool IsPassed,
    [property: Key(1)] string FinalVerdict,
    [property: Key(2)] string Message
) : IRosMessage;