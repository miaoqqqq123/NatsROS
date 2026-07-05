using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.MES;

// ==========================================
// 核心数据实体：单件产品生产记录
// ==========================================
[MessagePackObject]
public record ProductRecord(
    [property: Key(0)] string Barcode,
    [property: Key(1)] long Timestamp,
    [property: Key(2)] bool IsPass,
    [property: Key(3)] double CycleTimeSec,
    [property: Key(4)] double OffsetX,
    [property: Key(5)] double OffsetY, [property: Key(6)] double Angle
) : IRosMessage;

// MES 数据上传 RPC
[MessagePackObject]
public record UploadRecordReq(
    [property: Key(0)] ProductRecord Record
) : IRosRequest<UploadRecordRes>;

[MessagePackObject]
public record UploadRecordRes(
    [property: Key(0)] bool Success
) : IRosMessage;

// MES 数据查询 RPC (供大屏拉取)
public record GetRecordsReq(
    [property: Key(0)] int Limit = 1000
) : IRosRequest<GetRecordsRes>;

[MessagePackObject]
public record GetRecordsRes(
    [property: Key(0)] System.Collections.Generic.List<ProductRecord> Records
) : IRosMessage;