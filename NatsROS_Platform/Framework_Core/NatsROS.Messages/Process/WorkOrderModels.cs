using MessagePack;
using NatsROS.Core;

// 记得在 NatsROS.Messages 库的某处打上 [assembly: ContainsNatsRosPermissions] 等标签，
// 这里不需要重复打，只要保证在这个库里即可。

namespace NatsROS.Messages.Process;

// ==========================================
// 分布式加工订单契约 (Y1/Y2 发送给点胶中心)
// ==========================================
[MessagePackObject]
public record WorkOrderReq(
    [property: Key(0)] string SourceStation, // 来源工站，如 "Y1_Station"
    [property: Key(1)] string Barcode        // 产品的条码
) : IRosRequest<WorkOrderRes>;

[MessagePackObject]
public record WorkOrderRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Message
) : IRosMessage;