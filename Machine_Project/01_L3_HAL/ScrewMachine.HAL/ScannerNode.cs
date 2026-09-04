using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using ScrewMachine.Messages.Hardware;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.HAL
{
    [RosNode(DisplayName = "基恩士扫码枪", Category = "数据采集 (DAQ)", Description = "模拟 TCP/IP 或串口扫码枪的数据返回延时")]
    public class ScannerNode(INatsClient nats, string nodeName, ILogger<ScannerNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string prefix = Parameters.GetLocal("BarcodePrefix", "PROD");

            //激光状态发布者
            var flashPub = CreatePublisher<ScannerFlashMsg>("hw.scanner.flash", RosQosProfile.SensorData);

            var scanServer = CreateServer<TriggerScanReq, TriggerScanRes>($"{Name}.trigger");
            _ = scanServer.ServeAsync(async req =>
            {
                Logger.LogInformation("🔍 扫码枪触发！激光开启，正在解析条码...");
                string prefix = Parameters.GetLocal("BarcodePrefix", "PROD");

                // 1. 广播：开启红色激光！
                await flashPub.PublishAsync(new ScannerFlashMsg(Name, true), stoppingToken);

                // 模拟相机解码的物理延迟
                await Task.Delay(Random.Shared.Next(300, 800), stoppingToken);

                // 2. 广播：关闭红色激光！
                await flashPub.PublishAsync(new ScannerFlashMsg(Name, false), stoppingToken);

                //// 模拟 10% 概率条码脏污扫不到
                //if (Random.Shared.NextDouble() < 0.1)
                //{
                //    Logger.LogWarning("⚠️ 条码脏污，解码失败 (No Read)！");
                //    return new TriggerScanRes(false, "", "NoRead");
                //}

                string barcode = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}";
                Logger.LogInformation("✅ 扫码成功: {Barcode}", barcode);

                return new TriggerScanRes(true, barcode);
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}