using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.Motion;

// ==========================================
// 轴运动 Action：目标、反馈、结果
// ==========================================

[MessagePackObject]
public record AxisMoveGoal(
    [property: Key(0)] double TargetPosition, // 目标绝对位置 (mm)
    [property: Key(1)] double Velocity        // 移动速度 (mm/s)
) : IRosActionGoal<AxisMoveFeedback, AxisMoveResult>; 

[MessagePackObject]
public record AxisMoveFeedback(
    [property: Key(0)] double CurrentPosition // 实时当前位置反馈
) : IRosMessage;

[MessagePackObject]
public record AxisMoveResult(
    [property: Key(0)] bool IsSuccess,
    [property: Key(1)] double FinalPosition,
    [property: Key(2)] string Message = ""
) : IRosMessage;