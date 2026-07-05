using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Core.Communication;
using NatsROS.Core.Lifecycle;
using NatsROS.Core.SystemMessages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Hosting;

/// <summary>
/// 融合了 NatsROS 节点能力和 .NET 后台服务生命周期的基类。
/// 在微服务模式下，所有业务节点应继承此类。
/// </summary>
public abstract class HostedRosNode(INatsClient nats, string nodeName, ILogger logger)
    : RosNode(nats, nodeName, logger), IHostedService
{
    private Task? _executeTask;
    private CancellationTokenSource? _stoppingCts;

    // APM 心跳相关组件
    private RosPublisher<NodeHeartbeatMsg>? _heartbeatPub;
    private Task? _heartbeatTask;
    private readonly DateTime _startTime = DateTime.Now;

    /// <summary> 
    /// Unconfigured -> Inactive (适合：读取参数、连接数据库、预加载 DLL) 
    /// </summary>
    protected virtual Task OnConfigureAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Inactive -> Active (适合：接通电机使能、正式开始广播数据) 
    /// </summary>
    protected virtual Task OnActivateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Active -> Inactive (适合：断开电机使能、暂停广播)
    /// </summary>
    protected virtual Task OnDeactivateAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary> 
    /// Inactive/Faulted -> Unconfigured (适合：释放串口句柄、清空内存) 
    /// </summary>
    protected virtual Task OnCleanupAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary> 
    /// 核心业务循环 (只有进入 Active 状态后才执行) 
    /// </summary>
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);


    // .NET 泛型主机启动时自动调用
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 【核心大招】：初始化心跳发布者，并启动静默的探针后台线程
        _heartbeatPub = CreatePublisher<NodeHeartbeatMsg>("sys.apm.heartbeat", RosQosProfile.SensorData);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_stoppingCts.Token));

        _executeTask = Task.Run(async () =>
        {
            try
            {
                Logger.LogInformation("⏳ 节点 [{NodeName}] 正在配置资源...", Name);
                await OnConfigureAsync(_stoppingCts.Token);
                ChangeState(NodeLifecycleState.Inactive);

                Logger.LogInformation("🟢 节点 [{NodeName}] 正在激活...", Name);
                await OnActivateAsync(_stoppingCts.Token);
                ChangeState(NodeLifecycleState.Active);

                await ExecuteAsync(_stoppingCts.Token);
                ChangeState(NodeLifecycleState.Finalized);
            }
            catch (OperationCanceledException) { ChangeState(NodeLifecycleState.Finalized); }
            catch (Exception ex)
            {
                Logger.LogCritical(ex, "❌ 节点 [{NodeName}] 发生致命异常，进入故障状态！", Name);
                ChangeState(NodeLifecycleState.Faulted);
            }
        });

        return Task.CompletedTask;
    }

    // ==========================================
    // APM 探针引擎：极其精准的 CPU 与内存采样
    // ==========================================
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // 避开系统刚启动时的高峰期
        await Task.Delay(2000, ct);

        var process = Process.GetCurrentProcess();
        TimeSpan lastProcessorTime = process.TotalProcessorTime;
        DateTime lastSampleTime = DateTime.UtcNow;
        int processorCount = Environment.ProcessorCount;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct); // 每 2 秒跳动一次心跳

                process.Refresh(); // 刷新进程统计信息

                // 1. 计算真实的 CPU 占用率 (相对于整个系统多核的百分比)
                TimeSpan currentProcessorTime = process.TotalProcessorTime;
                DateTime currentSampleTime = DateTime.UtcNow;

                double cpuUsedMs = (currentProcessorTime - lastProcessorTime).TotalMilliseconds;
                double totalMsPassed = (currentSampleTime - lastSampleTime).TotalMilliseconds;
                double cpuUsage = (cpuUsedMs / (processorCount * totalMsPassed)) * 100.0;

                lastProcessorTime = currentProcessorTime;
                lastSampleTime = currentSampleTime;

                // 2. 内存与线程采集
                double memMb = process.WorkingSet64 / 1024.0 / 1024.0;
                int threads = process.Threads.Count;
                double uptime = (DateTime.Now - _startTime).TotalSeconds;

                // 3. 广播心跳包
                if (_heartbeatPub != null)
                {
                    await _heartbeatPub.PublishAsync(new NodeHeartbeatMsg(
                        Name, (byte)CurrentState, cpuUsage, memMb, threads, uptime
                    ), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 探针自己崩了绝对不能影响业务主线程 */ }
        }
    }

    /// <summary>
    /// .NET 泛型主机关闭时自动调用（接收到 Ctrl+C 或 SIGTERM）
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null) return;

        try
        {
            // 1. 通知子类的业务循环停止
            _stoppingCts?.Cancel();

            // 严格执行反向状态机清理
            if (CurrentState == NodeLifecycleState.Active)
            {
                await OnDeactivateAsync(cancellationToken);
                ChangeState(NodeLifecycleState.Inactive);
            }

            if (CurrentState == NodeLifecycleState.Inactive || CurrentState == NodeLifecycleState.Faulted)
            {
                await OnCleanupAsync(cancellationToken);
                ChangeState(NodeLifecycleState.Unconfigured);
            }

            // 2. 调用底层 Core 的节点关闭逻辑 (关闭发布/订阅)
            Shutdown();
        }
        finally
        {
            // 等待节点安全退出，或者超时强杀
            await Task.WhenAny(_executeTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }
}
