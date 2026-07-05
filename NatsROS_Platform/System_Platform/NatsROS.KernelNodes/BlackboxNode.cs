using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Hosting;
using NatsROS.Messages.AEM;

namespace NatsROS.KernelNodes
{
    // ==========================================
    // 用于缓存在内存环中的原始报文结构
    // ==========================================
    public struct CachedMsg
    {
        public long TimestampTick;
        public string Subject;
        public byte[] Data; // 原始字节
        public string RosType; // 用于还原时的反序列化
    }

    [RosNode(DisplayName = "机台崩溃黑匣子 (Crash Dump)", Category = "系统核心 (System Core)", Description = "维护内存环状队列，遇致命报警自动进行延时快照打断，保存全息案发现场")]
    public class BlackboxNode(INatsClient nats, string nodeName, ILogger<BlackboxNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        // 核心双端队列 (作为环形缓冲)
        private readonly ConcurrentQueue<CachedMsg> _ringBuffer = new();

        // 内存熔断保护配置
        private readonly int _maxBufferSeconds = 60; // 记录发生前的 60 秒
        private readonly long _maxBufferBytes = 200 * 1024 * 1024; // 绝对红线：最多吃 200MB 内存！

        // 状态监控
        private long _currentBufferBytes = 0;
        private bool _isTriggered = false; // 是否已经触发了快照（此时停止淘汰旧数据）
        private string _dumpsDir = "";

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 黑匣子快照也必须锁死在本机专属区，不随工程迁移！
            _dumpsDir = NatsROS.Core.Environment.WorkspaceManager.GetCrashDumpsPath();
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("📼 黑匣子已开启！最大回溯时间:{Sec}s, 内存红线:{Mb}MB", _maxBufferSeconds, _maxBufferBytes / 1024 / 1024);

            // ==========================================
            // 1. 无脑监听全网所有流量 (上帝视角)
            // ==========================================
            var allTrafficSub = Nats.SubscribeAsync<byte[]>(">", serializer: NatsDefaultSerializerRegistry.Default.GetDeserializer<byte[]>(), cancellationToken: stoppingToken);
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in allTrafficSub)
                    {
                        // 忽略自己和底层 NATS 协议的心跳
                        if (msg.Subject.StartsWith("_INBOX.") || msg.Subject.StartsWith("sys.apm.")) continue;
                        if (msg.Data == null) continue;

                        string rosType = "未知";
                        if (msg.Headers != null && msg.Headers.TryGetValue("ros-type", out var typeVal))
                            rosType = typeVal.ToString();

                        var cachedMsg = new CachedMsg
                        {
                            TimestampTick = DateTime.UtcNow.Ticks,
                            Subject = msg.Subject,
                            Data = msg.Data,
                            RosType = rosType
                        };

                        _ringBuffer.Enqueue(cachedMsg);
                        Interlocked.Add(ref _currentBufferBytes, msg.Data.Length + 50); // 粗略加上字符串开销

                        // 【安全熔断阀】：如果没触发落盘，持续淘汰旧数据
                        if (!_isTriggered)
                        {
                            EnforceBufferLimits();
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, stoppingToken);

            // ==========================================
            // 2. 监听 AEM 系统，一旦有 Critical 报警，瞬间激活！
            // ==========================================
            var aemSub = CreateSubscriber<AlarmsChangedEvent>("aem.changed");
            _ = Task.Run(async () =>
            {
                await foreach (var msg in aemSub.SubscribeAsync(stoppingToken))
                {
                    if (msg != null && !_isTriggered)
                    {
                        // 寻找是否有新爆出来的 Critical 级别报警
                        foreach (var alarm in msg.ActiveAlarms)
                        {
                            if (alarm.Level == AlarmLevel.Critical && alarm.Status == AlarmStatus.Raised)
                            {
                                Logger.LogCritical("🚨 黑匣子侦测到致命报警 [{Code}]，启动坠机快照序列！", alarm.Code);
                                _ = PerformCrashDumpAsync($"ALARM_{alarm.Code}");
                                break; // 触发一次就够了
                            }
                        }
                    }
                }
            }, stoppingToken);

            // 3. 监听手动触发请求 (来自 HMI)
            var triggerSrv = CreateServer<TriggerDumpReq, TriggerDumpRes>($"{Name}.trigger");
            _ = triggerSrv.ServeAsync(req =>
            {
                if (_isTriggered) return Task.FromResult(new TriggerDumpRes(false, "正在进行快照，请勿重复触发"));
                Logger.LogWarning("📸 收到手动黑匣子快照请求，原因: {Reason}", req.Reason);
                _ = PerformCrashDumpAsync("MANUAL");
                return Task.FromResult(new TriggerDumpRes(true, "快照序列已启动"));
            }, stoppingToken);

            // 4. 定期汇报自己吃了多少内存 (让 APM 和大屏知道)
            var statusPub = CreatePublisher<DumpStatusMsg>("sys.blackbox.status", RosQosProfile.SensorData);
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await statusPub.PublishAsync(new DumpStatusMsg(_isTriggered, _ringBuffer.Count, _currentBufferBytes / 1024.0 / 1024.0), stoppingToken);
                    await Task.Delay(2000, stoppingToken);
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // ==========================================
        // 双重淘汰机制 (确保自身永不 OOM 崩溃)
        // ==========================================
        private void EnforceBufferLimits()
        {
            long cutoffTicks = DateTime.UtcNow.AddSeconds(-_maxBufferSeconds).Ticks;

            // 只要队列满了（内存超标），或者数据太旧（超过60秒），统统丢掉！
            while (_ringBuffer.TryPeek(out var oldest) &&
                  (oldest.TimestampTick < cutoffTicks || Interlocked.Read(ref _currentBufferBytes) > _maxBufferBytes))
            {
                if (_ringBuffer.TryDequeue(out var dequeued))
                {
                    Interlocked.Add(ref _currentBufferBytes, -(dequeued.Data.Length + 50));
                }
            }
        }

        // ==========================================
        // 核心落盘逻辑：截取未来 10 秒 + 打包成 Bag 文件
        // ==========================================
        private async Task PerformCrashDumpAsync(string reasonTag)
        {
            _isTriggered = true; // 锁定队列淘汰机制，开始完整记录余波

            try
            {
                Logger.LogInformation("📼 坠机快照已激活，正在记录事故发生后 10 秒的余波数据...");
                await Task.Delay(10000); // 录制接下来10秒

                string timestampStr = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dumpFolderName = $"Dump_{timestampStr}_{reasonTag}";
                string dumpFolderPath = Path.Combine(_dumpsDir, dumpFolderName);
                Directory.CreateDirectory(dumpFolderPath);

                // 1. 生成标准的 Bag 文件 (利用我们原生的 BinaryWriter 写盘)
                string bagFilePath = Path.Combine(dumpFolderPath, "network_traffic.bag");
                long savedCount = 0;

                using (var fs = new FileStream(bagFilePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(fs))
                {
                    // 【极度细节】：先把队列里用到的所有类型的元数据，放在开头写进去！(兼容我们的 BagPlayer)
                    var typeDict = new System.Collections.Generic.HashSet<string>();
                    foreach (var msg in _ringBuffer) typeDict.Add($"{msg.Subject}|{msg.RosType}");

                    foreach (var typeMeta in typeDict)
                    {
                        var parts = typeMeta.Split('|');
                        writer.Write(-1L); // Magic Tick = -1 代表元数据
                        writer.Write(parts[0]); // Topic
                        writer.Write(parts[1]); // Type
                    }

                    // 开始写入真实包
                    while (_ringBuffer.TryDequeue(out var msg))
                    {
                        writer.Write(msg.TimestampTick);
                        writer.Write(msg.Subject);
                        writer.Write(msg.Data.Length);
                        writer.Write(msg.Data);
                        savedCount++;
                    }
                }

                // 2. 拷贝关键上下文文件 (报警日志、配方库等，方便打包发给售后)
                string rootDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] filesToCopy = { "alarms.json", "rms_recipes.json", "nlog.config" };
                foreach (var f in filesToCopy)
                {
                    string src = Path.Combine(rootDir, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dumpFolderPath, f), true);
                }

                // 3. 将整个文件夹打成一个 ZIP 包
                string zipPath = Path.Combine(_dumpsDir, $"{dumpFolderName}.zip");
                ZipFile.CreateFromDirectory(dumpFolderPath, zipPath);

                // 打包完后删掉原文件夹，保持硬盘干净
                Directory.Delete(dumpFolderPath, true);

                Logger.LogInformation("🎉 坠机快照生成完毕！共保存 {Count} 条报文，文件位于: {Path}", savedCount, zipPath);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "❌ 黑匣子落盘失败！");
            }
            finally
            {
                // 重置状态，重新开始记录
                Interlocked.Exchange(ref _currentBufferBytes, 0);
                while (_ringBuffer.TryDequeue(out _)) { } // 清空队列
                _isTriggered = false;
            }
        }
    }
}