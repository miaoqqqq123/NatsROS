using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core.Communication;

public enum RosReliability
{
    BestEffort, // 尽力而为 (Core NATS)
    Reliable    // 可靠传输 (NATS JetStream)
}

public enum RosDurability
{
    Volatile,      // 挥发性 (不保存历史)
    TransientLocal // 瞬态本地 (为后来者保留历史消息)
}

/// <summary>
/// ROS 2 服务质量配置映射
/// </summary>
public record RosQosProfile(RosReliability Reliability, RosDurability Durability, int HistoryDepth = 10)
{
    // 默认传感器配置：快，但不保证送达
    public static RosQosProfile SensorData => new(RosReliability.BestEffort, RosDurability.Volatile, 5);

    // 默认关键指令配置：可靠送达，且后来者可以拉取到最后的历史状态
    public static RosQosProfile Reliable => new(RosReliability.Reliable, RosDurability.TransientLocal, 10);
}

