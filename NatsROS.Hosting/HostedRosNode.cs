using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core;
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

    /// <summary>
    /// 留给子类实现的核心业务逻辑（如循环发布数据、订阅话题等）
    /// </summary>
    protected abstract Task ExecuteAsync(CancellationToken stoppingToken);

    // .NET 泛型主机启动时自动调用
    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 将节点的执行逻辑放到后台线程运行，避免阻塞主机的启动过程
        _executeTask = ExecuteAsync(_stoppingCts.Token);

        return _executeTask.IsCompleted ? _executeTask : Task.CompletedTask;
    }

    // .NET 泛型主机关闭时自动调用（接收到 Ctrl+C 或 SIGTERM）
    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executeTask == null) return;

        try
        {
            // 1. 通知子类的业务循环停止
            _stoppingCts?.Cancel();

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
