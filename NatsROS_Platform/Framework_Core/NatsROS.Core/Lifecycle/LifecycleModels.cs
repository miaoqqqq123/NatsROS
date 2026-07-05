using System;
using MessagePack;

namespace NatsROS.Core.Lifecycle;

// ==========================================
// 1. ROS 2 标准节点生命周期状态
// ==========================================
public enum NodeLifecycleState : byte
{
    Unconfigured = 0, // 未配置 (刚实例化，未读参数，未连硬件)
    Inactive = 1,     // 待机 (已读参数，就绪，但不向外发数据)
    Active = 2,       // 运行中 (火力全开，正常工作中)
    Faulted = 3,      // 故障 (发生未捕获的严重异常)
    Finalized = 4     // 已终结 (安全退出，资源已释放)
}

// ==========================================
// 2. 节点崩溃时的重启策略
// ==========================================
public enum NodeRestartPolicy : byte
{
    Never = 0,     // 绝不重启 (适合危险的物理硬件：如机械臂，死机必须人工确认)
    OnFailure = 1, // 异常时重启 (适合视觉、网络通信节点)
    Always = 2     // 总是常驻 (就算正常退出也会被拉起)
}

// ==========================================
// 3. 自愈策略配置模型
// ==========================================
[MessagePackObject]
public record NodeRecoveryOptions(
    [property: Key(0)] NodeRestartPolicy Policy = NodeRestartPolicy.Never,
    [property: Key(1)] int MaxRetries = 3, 
    [property: Key(2)] int DelaySeconds = 5
) : IRosMessage;