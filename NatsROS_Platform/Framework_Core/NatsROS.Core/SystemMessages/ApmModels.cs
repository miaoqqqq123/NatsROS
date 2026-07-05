using MessagePack;
using System;

namespace NatsROS.Core.SystemMessages;

[MessagePackObject]
public record NodeHeartbeatMsg(
    [property: Key(0)] string NodeName,
    [property: Key(1)] byte LifecycleState,        // 当前生命周期状态 (Unconfigured, Active等)
    [property: Key(2)] double CpuUsagePercent,     // CPU 占用率 (0~100%)
    [property: Key(3)] double MemoryWorkingSetMb,  // 内存物理占用 (MB)
    [property: Key(4)] int ThreadCount,            // 进程当前开辟的线程数
    [property: Key(5)] double UptimeSeconds        // 节点存活时间 (秒)
) : IRosMessage;