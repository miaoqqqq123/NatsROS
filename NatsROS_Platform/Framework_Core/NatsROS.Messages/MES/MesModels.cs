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

    //取消固定的 OffsetX/Y，改为支持无限扩展的双字典结构
    [property: Key(4)] Dictionary<string, double> NumericMetrics, // 用于散点图/折线图
    [property: Key(5)] Dictionary<string, string> StringInfos     // 用于报表明细与追溯
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