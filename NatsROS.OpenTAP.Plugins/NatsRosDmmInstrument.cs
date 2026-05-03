using OpenTap;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.Communication;
using NatsROS.Core.Serialization;
using NatsROS.Messages.ATE;
using System;

namespace NatsROS.OpenTAP.Plugins
{
    [Display("NatsROS 万用表代理", Group: "NatsROS Instruments", Description: "通过 NATS 网络远程控制的虚拟万用表")]
    public class NatsRosDmmInstrument : Instrument
    {
        [Display("NATS 核心网络地址", Group: "网络配置", Order: 1)]
        public string NatsUrl { get; set; } = "nats://127.0.0.1:4222";

        [Display("服务寻址路由", Group: "网络配置", Order: 2)]
        public string ServiceRoute { get; set; } = "dmm.measure_voltage";

        private INatsClient _nats;
        private RosServiceClient<MeasureVoltageReq, MeasureVoltageRes> _measureClient;

        public NatsRosDmmInstrument()
        {
            Name = "NatsDMM"; // 在 OpenTAP 界面里的默认名字
        }

        // OpenTAP 点击连接仪器时触发
        public override void Open()
        {
            base.Open();

            // 1. 挂载 NatsROS 专用的序列化器，连接到 NATS 宇宙
            var options = NatsOpts.Default with
            {
                Url = NatsUrl,
                SerializerRegistry = new NatsRosSerializerRegistry()
            };

            _nats = new NatsClient(options);
            _nats.ConnectAsync().GetAwaiter().GetResult(); // 同步阻塞等待连接成功

            // 2. 创建指向底层 Container 物理节点的 RPC 客户端
            _measureClient = new RosServiceClient<MeasureVoltageReq, MeasureVoltageRes>(_nats, ServiceRoute);

            Log.Info($"[NatsROS] 已成功桥接到远程硬件节点: {ServiceRoute}");
        }

        // OpenTAP 断开连接时触发
        public override void Close()
        {
            _nats?.DisposeAsync().GetAwaiter().GetResult();
            base.Close();
            Log.Info("[NatsROS] 虚拟万用表已断开 NATS 网络。");
        }

        // 暴露给 TestStep 调用的强类型业务方法
        public double MeasureVoltage(int timeoutSeconds = 5)
        {
            // 极度优雅：把 OpenTAP 的同步时序调用，瞬间转化为跨网络的 RPC 异步调用！
            var res = _measureClient.CallAsync(
                new MeasureVoltageReq(),
                TimeSpan.FromSeconds(timeoutSeconds)
            ).GetAwaiter().GetResult();

            if (res == null || !res.IsSuccess)
            {
                throw new Exception($"[NatsROS] 远程硬件节点 '{ServiceRoute}' 测量失败或响应超时！");
            }

            return res.Voltage;
        }
    }
}