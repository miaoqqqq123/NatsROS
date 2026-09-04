using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Hosting;
using NatsROS.KernelNodes.Database;
using NatsROS.Messages.MES;
using System.Text.Json;
using System.Threading.Channels;

namespace NatsROS.KernelNodes
{
    [RosNode(DisplayName = "本地 MES 数据库节点", Category = "数据系统 (Data)", Description = "基于 EF Core 和 Channels 缓冲的高并发落盘引擎")]
    public class MesDatabaseNode(INatsClient nats, string nodeName, ILogger<MesDatabaseNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 核心黑魔法：无锁内存通道，用于削峰填谷
        private readonly Channel<ProductRecord> _insertChannel = Channel.CreateUnbounded<ProductRecord>();

        protected override async Task OnConfigureAsync(CancellationToken ct)
        {
            using var db = new MesDbContext();
            await db.Database.EnsureCreatedAsync(ct);

            // 【性能核弹】：开启 SQLite WAL (预写日志) 模式！
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", ct);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("💾 工业级 MES 存储节点 [{NodeName}] 启动！", Name);

            // 1. 开启后台的批量搬运工
            _ = Task.Run(() => BulkInsertWorkerAsync(stoppingToken), stoppingToken);

            // 2. 接收上传：直接塞入内存队列，耗时 0.001 毫秒
            var uploadServer = CreateServer<UploadRecordReq, UploadRecordRes>($"{Name}.upload");
            _ = uploadServer.ServeAsync(async req =>
            {
                await _insertChannel.Writer.WriteAsync(req.Record, stoppingToken);
                return new UploadRecordRes(true);
            }, stoppingToken);

            // 3. 极速只读查询
            var queryServer = CreateServer<GetRecordsReq, GetRecordsRes>($"{Name}.query");
            _ = queryServer.ServeAsync(async req =>
            {
                using var db = new MesDbContext();
                var entities = await db.MesRecords.AsNoTracking().OrderByDescending(x => x.Timestamp).Take(req.Limit).ToListAsync(stoppingToken);

                var records = entities.Select(e => new ProductRecord(
                    e.Barcode, e.Timestamp, e.IsPass, e.CycleTimeSec,
                    JsonSerializer.Deserialize<Dictionary<string, double>>(e.MetricsJson) ?? new(),
                    JsonSerializer.Deserialize<Dictionary<string, string>>(e.InfosJson) ?? new()
                )).ToList();

                return new GetRecordsRes(records);
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        private async Task BulkInsertWorkerAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 阻塞等待数据到来
                    var record = await _insertChannel.Reader.ReadAsync(ct);
                    var entities = new List<MesRecordEntity> { ConvertToEntity(record) };

                    // 贪婪提取：如果在极短时间内堆积了多条，一次性全拿出来！
                    while (_insertChannel.Reader.TryRead(out var moreRecord))
                    {
                        entities.Add(ConvertToEntity(moreRecord));
                        if (entities.Count >= 500) break; // 每批最多 500 条
                    }

                    using var db = new MesDbContext();
                    await db.MesRecords.AddRangeAsync(entities, ct);
                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Logger.LogError(ex, "落盘异常"); await Task.Delay(1000, ct); }
            }
        }

        private MesRecordEntity ConvertToEntity(ProductRecord r) => new MesRecordEntity
        {
            Barcode = r.Barcode,
            Timestamp = r.Timestamp,
            IsPass = r.IsPass,
            CycleTimeSec = r.CycleTimeSec,
            MetricsJson = JsonSerializer.Serialize(r.NumericMetrics),
            InfosJson = JsonSerializer.Serialize(r.StringInfos)
        };
    }
}