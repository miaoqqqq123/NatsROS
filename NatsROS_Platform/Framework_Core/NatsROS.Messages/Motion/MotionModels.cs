using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.Motion;

// ==========================================
// 1. 轴实时状态广播 (高频 Topic)
// ==========================================
[MessagePackObject]
public record AxisStateMsg(
    [property: Key(0)] double ActualPosition, // 当前实际物理位置 (mm或度)
    [property: Key(1)] double ActualVelocity, // 当前实时速度
    [property: Key(2)] bool IsServoOn,        // 使能状态 (脱机/激磁)
    [property: Key(3)] bool IsMoving,         // 是否正在运动
    [property: Key(4)] bool IsAlarm,          // 驱动器是否报警
    [property: Key(5)] bool LimitPositive,    // 触碰到正限位
    [property: Key(6)] bool LimitNegative,    // 触碰到负限位
    [property: Key(7)] int ErrorCode = 0      // 硬件原始错误码（供查手册）
) : IRosMessage;

// ==========================================
// 2. 瞬态控制指令 (Service RPC)
// ==========================================

// 使能/脱机 (Servo On/Off)
[MessagePackObject]
public record AxisEnableReq(
    [property: Key(0)] bool Enable
) : IRosRequest<AxisEnableRes>;

[MessagePackObject]
public record AxisEnableRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string Message = ""
) : IRosMessage;

// 清除驱动器报警 (Reset)
[MessagePackObject]
public record AxisResetReq() : IRosRequest<AxisResetRes>;

[MessagePackObject]
public record AxisResetRes(
    [property: Key(0)] bool Success
) : IRosMessage;

// 轴停止 (急停 / 平滑停止)
public enum AxisStopMode : byte 
{ 
    Smooth = 0, 
    Emergency = 1 
}

[MessagePackObject]
public record AxisStopReq(
    [property: Key(0)] AxisStopMode Mode
) : IRosRequest<AxisStopRes>;

[MessagePackObject]
public record AxisStopRes(
    [property: Key(0)] bool Success
) : IRosMessage;


// ==========================================
// 3. 长耗时运动指令 (Action 动作)
// ==========================================

// 3.1 回零动作 (Homing)
[MessagePackObject]
public record AxisHomeGoal(
    [property: Key(0)] double SearchVelocity,
    [property: Key(1)] int HomeMode = 0 // 0: 原点开关, 1: 负限位+Z相 等
) : IRosActionGoal<AxisHomeFeedback, AxisHomeResult>;

[MessagePackObject]
public record AxisHomeFeedback(
    [property: Key(0)] string CurrentStage
) : IRosMessage;

[MessagePackObject]
public record AxisHomeResult(
    [property: Key(0)] bool IsSuccess, 
    [property: Key(1)] string Message = ""
) : IRosMessage;

// 3.2 绝对/相对点位移动 (PTP Move)
public enum MoveMode : byte 
{
    Absolute = 0, 
    Relative = 1 
}

[MessagePackObject]
public record AxisMoveGoal(
    [property: Key(0)] double TargetPosition,
    [property: Key(1)] double Velocity,
    [property: Key(2)] MoveMode Mode = MoveMode.Absolute
) : IRosActionGoal<AxisMoveFeedback, AxisMoveResult>;

[MessagePackObject]
public record AxisMoveFeedback(
    [property: Key(0)] double CurrentPosition
) : IRosMessage;

[MessagePackObject]
public record AxisMoveResult(
    [property: Key(0)] bool IsSuccess, 
    [property: Key(1)] double FinalPosition, 
    [property: Key(2)] string Message = ""
) : IRosMessage;

// 3.3 连续点动 (JOG)
[MessagePackObject]
public record AxisJogGoal(
    [property: Key(0)] double Velocity // 正数正向转，负数反向转。停止时直接发送 ActionCancelReq 打断即可！
) : IRosActionGoal<AxisJogFeedback, AxisJogResult>;

[MessagePackObject]
public record AxisJogFeedback(
    [property: Key(0)] double CurrentPosition
) : IRosMessage;

[MessagePackObject]
public record AxisJogResult(
    [property: Key(0)] bool IsSuccess
) : IRosMessage;