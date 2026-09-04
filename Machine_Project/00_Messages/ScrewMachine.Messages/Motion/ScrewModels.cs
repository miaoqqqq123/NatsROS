using MessagePack;
using NatsROS.Core;

namespace ScrewMachine.Messages.Motion;

// ==========================================
// 打螺丝专用 Action 契约
// ==========================================
[MessagePackObject]
public record DriveScrewGoal(
    [property: Key(0)] double TargetTorque, 
    [property: Key(1)] int TargetRpm
) : IRosActionGoal<DriveScrewFeedback, DriveScrewResult>; // 【修改点：强绑定反馈与结果】[MessagePackObject]

public record DriveScrewFeedback(
    [property: Key(0)] double CurrentTorque, 
    [property: Key(1)] double CurrentDepth
) : IRosMessage;

[MessagePackObject]
public record DriveScrewResult(
    [property: Key(0)] bool IsSuccess, 
    [property: Key(1)] string ErrorCode = ""
) : IRosMessage;