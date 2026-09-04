using MessagePack;
using NatsROS.Core;
using NatsROS.Messages.GeometryMsgs;

// 引入 Vector3

namespace ScrewMachine.Messages.Motion;

// ==========================================
// 1. 点胶阀门控制 (RPC 服务)
// ==========================================
[MessagePackObject]
public record ValveControlReq(
    [property: Key(0)] bool Open
) : IRosRequest<ValveControlRes>; 

[MessagePackObject]
public record ValveControlRes(
    [property: Key(0)] bool Success
) : IRosMessage;

// ==========================================
// 2. 单点移动 Action (PTP)
// ==========================================
[MessagePackObject]
public record DispenserMoveGoal(
    [property: Key(0)] double TargetX,
    [property: Key(1)] double TargetY,
    [property: Key(2)] double TargetZ,
    [property: Key(3)] double Velocity,
    [property: Key(4)] string TargetYAxis  // 【新增】：指定联动哪个Y轴
) : IRosActionGoal<DispenserMoveFeedback, DispenserMoveResult>;

// ==========================================
// 3. 连续轨迹 Action (CP)
// ==========================================
[MessagePackObject]
public record DispenserTrajectoryGoal(
    [property: Key(0)] Vector3[] Path,     // 轨迹点阵列
    [property: Key(1)] double Velocity,
    [property: Key(2)] string TargetYAxis // 【新增】：指定联动哪个Y轴
) : IRosActionGoal<DispenserMoveFeedback, DispenserMoveResult>;

// ==========================================
// 通用反馈与结果
// ==========================================
[MessagePackObject]
public record DispenserMoveFeedback(
    [property: Key(0)] double CurrentX,
    [property: Key(1)] double CurrentY, 
    [property: Key(2)] double CurrentZ,
    [property: Key(3)] bool IsValveOpen,
    [property: Key(4)] string ActiveYAxis // 【新增】：向全网广播正在动哪个Y
) : IRosMessage;

[MessagePackObject]
public record DispenserMoveResult(
    [property: Key(0)] bool IsSuccess, 
    [property: Key(1)] string Message = ""
) : IRosMessage;