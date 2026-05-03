using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core;
using NatsROS.Core.Lifecycle;
using System;
using System.Collections.Generic;
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

    // ==========================================
    // 生命周期虚方法钩子 (Lifecycle Hooks)
    // 业务节点可以按需重写这些方法，安全地管理资源
    // ==========================================

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

        // 将节点的生命周期流转放到后台执行，绝不阻塞母体启动
        _executeTask = Task.Run(async () =>
        {
            try
            {
                // 1. 配置阶段 (Configure)
                Logger.LogInformation("⏳ 节点 [{NodeName}] 正在配置资源...", Name);
                await OnConfigureAsync(_stoppingCts.Token);
                ChangeState(NodeLifecycleState.Inactive); // 配置成功，进入待机

                // 2. 激活阶段 (Activate)
                Logger.LogInformation("🟢 节点 [{NodeName}] 正在激活...", Name);
                await OnActivateAsync(_stoppingCts.Token);
                ChangeState(NodeLifecycleState.Active); // 激活成功，火力全开

                // 3. 执行核心业务 (Execute)
                await ExecuteAsync(_stoppingCts.Token);

                // 4. 正常结束
                ChangeState(NodeLifecycleState.Finalized);
            }
            catch (OperationCanceledException)
            {
                ChangeState(NodeLifecycleState.Finalized);
            }
            catch (Exception ex)
            {
                // 【致命异常】：任何一个阶段抛出未捕获异常，立刻进入 Faulted 状态，触发母体自愈！
                Logger.LogCritical(ex, "❌ 节点 [{NodeName}] 发生致命异常，进入故障状态！", Name);
                ChangeState(NodeLifecycleState.Faulted);
            }
        });

        return Task.CompletedTask;
    }

    // .NET 泛型主机关闭时自动调用（接收到 Ctrl+C 或 SIGTERM）
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
