using System.Text.Json;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using NatsROS.Messages.MES;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "本地 MES 边缘网关", Category = "数据系统 (Data)", Description = "接收测试结果并持久化到本地存储，提供报表查询")]
    public class MockMesNode(INatsClient nats, string nodeName, ILogger<MockMesNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private List<ProductRecord> _database = new();
        private string _dbFilePath = "";

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 数据库存放在运行目录下
            _dbFilePath = NatsROS.Core.Environment.WorkspaceManager.GetLocalDatabasePath("local_mes_db.json");
            if (File.Exists(_dbFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_dbFilePath);
                    var loaded = JsonSerializer.Deserialize<List<ProductRecord>>(json);
                    if (loaded != null) _database = loaded;
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("💾 本地 MES 节点 [{NodeName}] 启动！当前数据库共 {Count} 条记录。", Name, _database.Count);

            // 1. 监听上传数据请求
            var uploadServer = CreateServer<UploadRecordReq, UploadRecordRes>($"{Name}.upload");
            _ = uploadServer.ServeAsync(req =>
            {
                Logger.LogInformation("📥 收到生产数据上传！条码: {Barcode}, 耗时: {Time:F1}s, 结果: {Res}", req.Record.Barcode, req.Record.CycleTimeSec, req.Record.IsPass ? "PASS" : "FAIL");
                _database.Add(req.Record);

                // 异步存盘
                _ = Task.Run(() => File.WriteAllText(_dbFilePath, JsonSerializer.Serialize(_database)));

                return Task.FromResult(new UploadRecordRes(true));
            }, stoppingToken);

            // 2. 监听大屏查询请求
            var queryServer = CreateServer<GetRecordsReq, GetRecordsRes>($"{Name}.query");
            _ = queryServer.ServeAsync(req =>
            {
                Logger.LogInformation("📤 Dashboard 正在拉取生产报表数据...");
                return Task.FromResult(new GetRecordsRes(_database));
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}