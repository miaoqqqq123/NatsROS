using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Hosting;
using NatsROS.Messages.ATE;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NatsROS.Examples
{
    // ==========================================
    // 智能数字万用表 (硬件驱动节点)
    // 运行在 NatsROS.Container 中，负责真实的物理采样
    // ==========================================
    public class SmartDmmNode(INatsClient nats, string nodeName, ILogger<SmartDmmNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🔌 智能万用表 [{NodeName}] 硬件初始化完成，正在监听测试指令...", Name);

            // 对外暴露标准的 RPC 测量服务
            var measureServer = CreateServer<MeasureVoltageReq, MeasureVoltageRes>("dmm.measure_voltage");

            // 持续监听来自 OpenTAP (或其他任何客户端) 的测量请求
            await measureServer.ServeAsync(async req =>
            {
                Logger.LogInformation("⚡ 收到 ATE 测试引擎测量请求，正在读取底层硬件...");

                // 模拟真实仪器的总线通信与采样耗时 (300毫秒)
                await Task.Delay(300, stoppingToken);

                // 模拟生成一个逼真的电压值 (比如 5.0V 左右产生 ±0.025V 的微小波动)
                double simulatedVoltage = 5.0 + (Random.Shared.NextDouble() * 0.05 - 0.025);

                Logger.LogInformation("✅ 硬件采样完成，真实电压值: {Voltage:F4} V", simulatedVoltage);

                // 返回结果给 OpenTAP
                return new MeasureVoltageRes(simulatedVoltage, true);

            }, stoppingToken);
        }
    }
}
