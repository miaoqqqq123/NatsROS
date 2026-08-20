using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.Hardware;

// 塔灯显示状态枚举
public enum LightState : byte
{
    Off = 0,        // 熄灭
    Solid = 1,      // 常亮
    Blinking = 2    // 闪烁
}

// ==========================================
// 工业三色灯 (塔灯) 语义化控制指令
// ==========================================
[MessagePackObject]
public record SetTowerLightReq(
    [property: Key(0)] LightState Red,
    [property: Key(1)] LightState Yellow,
    [property: Key(2)] LightState Green,
    [property: Key(3)] bool Buzzer         // 蜂鸣器开关
) : IRosRequest<SetTowerLightRes>;

[MessagePackObject]
public record SetTowerLightRes(
    [property: Key(0)] bool Success
) : IRosMessage;