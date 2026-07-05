using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.AEM;

[MessagePackObject]
public record DumpStatusMsg(
    [property: Key(0)] bool IsDumping,          // 当前是否正在执行延时落盘
    [property: Key(1)] int BufferedMessageCount,// 内存环里目前存了多少条报文
    [property: Key(2)] double BufferSizeMb      // 内存环当前占用的估算物理大小 (MB)
) : IRosMessage;

// 供 HMI 手动触发“一键快照”的 RPC
[MessagePackObject]
public record TriggerDumpReq(
    [property: Key(0)] string Reason
) : IRosRequest<TriggerDumpRes>;

[MessagePackObject]
public record TriggerDumpRes(
    [property: Key(0)] bool Success, 
    [property: Key(1)] string SavedPath
) : IRosMessage;