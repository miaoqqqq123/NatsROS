using MessagePack;
using NatsROS.Core;

namespace NatsROS.Messages.Hardware;

// ==========================================
// 扫码枪触发与读取契约
// ==========================================
[MessagePackObject]
public record TriggerScanReq(
    [property: Key(0)] int TimeoutMs = 3000 // 允许的最大扫码等待时间
) : IRosRequest<TriggerScanRes>;

[MessagePackObject]
public record TriggerScanRes(
    [property: Key(0)] bool Success,
    [property: Key(1)] string Barcode,     // 扫到的条码内容
    [property: Key(2)] string Message = "" // 如果失败，返回原因（如 "超时未扫到"）
) : IRosMessage;

// 扫码枪激光闪烁广播
[MessagePackObject]
public record ScannerFlashMsg(
    [property: Key(0)] string ScannerName,
    [property: Key(1)] bool IsFlashing
) : IRosMessage;