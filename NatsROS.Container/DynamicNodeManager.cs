using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NatsROS.Container.Models;
using NatsROS.Core.Lifecycle;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using System.Collections.Concurrent;
using System.Reflection;

namespace NatsROS.Container;

/// <summary>
/// 动态节点管理器：负责通过反射实例化节点，并接管它们的生命周期与崩溃自愈
/// </summary>
public class DynamicNodeManager(IServiceProvider serviceProvider, ILogger<DynamicNodeManager> logger)
{
    // 存储当前正在运行的节点实例
    private readonly ConcurrentDictionary<string, HostedRosNode> _runningNodes = new();

    // 记录各个节点已重试的次数 (防止无限死循环重启)
    private readonly ConcurrentDictionary<string, int> _retryCounts = new();

    // 记录节点的启动配置，用于崩溃后按原配置拉起
    private readonly ConcurrentDictionary<string, NodeLaunchConfig> _nodeConfigs = new();

    // ==========================================
    // 1. 核心加载引擎 (带参数注入与事件绑定)
    // ==========================================
    public async Task<(bool Success, string Message)> LoadNodeFromConfigAsync(NodeLaunchConfig config)
    {
        if (_runningNodes.ContainsKey(config.NodeName))
            return (false, $"节点 '{config.NodeName}' 已经在运行了！");

        try
        {
            var targetType = Type.GetType($"{config.TypeName}, {config.AssemblyName}");
            if (targetType == null) return (false, $"找不到类型: {config.TypeName}");

            // 利用 DI 容器实例化节点
            var nodeInstance = (HostedRosNode)ActivatorUtilities.CreateInstance(serviceProvider, targetType, config.NodeName);

            // 【注入 1】：赋予重启策略
            nodeInstance.RecoveryOptions = new NodeRecoveryOptions(config.RestartPolicy, config.MaxRetries, config.RestartDelaySeconds);

            // 【注入 2】：在启动前，强行将配置文件里的参数灌入节点的 Parameter Server 中！
            if (config.Parameters != null)
            {
                foreach (var kvp in config.Parameters)
                {
                    nodeInstance.Parameters.SetLocal(kvp.Key, kvp.Value);
                }
            }

            // 【注入 3】：监听它的生死状态
            nodeInstance.OnStateChanged += Node_OnStateChanged;

            // 记录档案
            _nodeConfigs[config.NodeName] = config;
            _runningNodes[config.NodeName] = nodeInstance;

            // 启动节点
            await nodeInstance.StartAsync(CancellationToken.None);

            logger.LogInformation("✅ 成功加载节点: [{NodeName}] (策略: {Policy})", config.NodeName, config.RestartPolicy);
            return (true, "启动成功");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ 加载节点 [{NodeName}] 失败！", config.NodeName);
            return (false, $"启动异常: {ex.Message}");
        }
    }

    // ==========================================
    // 2. 崩溃自愈守护进程 (Auto-Recovery Daemon)
    // ==========================================
    private async void Node_OnStateChanged(string nodeName, NodeLifecycleState state)
    {
        // 只有当节点状态变成 Faulted 时，我们才介入抢救
        if (state == NodeLifecycleState.Faulted)
        {
            if (_nodeConfigs.TryGetValue(nodeName, out var config) && _runningNodes.TryRemove(nodeName, out var deadNode))
            {
                // 卸载死掉的节点尸体
                deadNode.OnStateChanged -= Node_OnStateChanged;
                deadNode.Shutdown();

                var policy = deadNode.RecoveryOptions.Policy;
                if (policy == NodeRestartPolicy.Never)
                {
                    logger.LogWarning("⚠️ 节点 [{NodeName}] 已阵亡。策略为 Never，放弃抢救。", nodeName);
                    return;
                }

                int currentRetries = _retryCounts.GetOrAdd(nodeName, 0);

                if (policy == NodeRestartPolicy.OnFailure && currentRetries >= deadNode.RecoveryOptions.MaxRetries)
                {
                    logger.LogError("💀 节点[{NodeName}] 连续崩溃达 {Max} 次，已放弃抢救！请人工介入。", nodeName, deadNode.RecoveryOptions.MaxRetries);
                    return;
                }

                _retryCounts[nodeName] = currentRetries + 1;
                int delay = deadNode.RecoveryOptions.DelaySeconds;

                logger.LogWarning("🚑 节点[{NodeName}] 触发自愈机制！将在 {Delay} 秒后进行第 {N} 次重启...", nodeName, delay, currentRetries + 1);

                // 等待指定的延迟时间
                await Task.Delay(TimeSpan.FromSeconds(delay));

                // 像凤凰涅槃一样，用当年的原配方重新拉起它！
                await LoadNodeFromConfigAsync(config);
            }
        }
    }

    // ==========================================
    // 3. 兼容旧的 RPC 加载指令 (从 Dashboard 发来的指令)
    // ==========================================
    public Task<(bool Success, string Message)> LoadAndStartNodeAsync(string assemblyName, string typeName, string nodeName)
    {
        // 包装成默认的无自愈配置 (Dashboard 手动加载的节点，默认不自动重启)
        var config = new NodeLaunchConfig { AssemblyName = assemblyName, TypeName = typeName, NodeName = nodeName };
        return LoadNodeFromConfigAsync(config);
    }

    // ==========================================
    // 3. 兼容从 Dashboard 传来的增强版 RPC 指令
    // ==========================================
    public Task<(bool Success, string Message)> LoadNodeFromReqAsync(NatsROS.Core.SystemMessages.LoadNodeReq req)
    {
        var config = new NatsROS.Container.Models.NodeLaunchConfig
        {
            AssemblyName = req.AssemblyName,
            TypeName = req.TypeName,
            NodeName = req.NodeName,
            Parameters = req.Parameters ?? new Dictionary<string, string>(),
            RestartPolicy = (NatsROS.Core.Lifecycle.NodeRestartPolicy)req.RestartPolicy,
            MaxRetries = req.MaxRetries,
            RestartDelaySeconds = req.RestartDelaySeconds
        };
        return LoadNodeFromConfigAsync(config);
    }

    public async Task<(bool Success, string Message)> StopAndUnloadNodeAsync(string nodeName)
    {
        if (_runningNodes.TryRemove(nodeName, out var nodeInstance))
        {
            _nodeConfigs.TryRemove(nodeName, out _);
            _retryCounts.TryRemove(nodeName, out _); // 清空重试计数

            nodeInstance.OnStateChanged -= Node_OnStateChanged;
            await nodeInstance.StopAsync(CancellationToken.None);

            logger.LogInformation("⏹️ 成功卸载节点: [{NodeName}]", nodeName);
            return (true, "卸载成功");
        }
        return (false, $"未找到名为 '{nodeName}' 的节点。");
    }

    // 提取节点名和真实的状态枚举
    public NodeStatusInfo[] GetRunningNodes() =>
        _runningNodes.Select(kv => new NodeStatusInfo(kv.Key, (byte)kv.Value.CurrentState)).ToArray();

    // 手动切换状态
    public Task<(bool Success, string Message)> ChangeNodeStateAsync(string nodeName, byte targetState)
    {
        if (_runningNodes.TryGetValue(nodeName, out var node))
        {
            node.ChangeState((NodeLifecycleState)targetState);
            return Task.FromResult((true, "状态已切换"));
        }
        return Task.FromResult((false, "节点不存在"));
    }

    public async Task ShutdownAllAsync()
    {
        foreach (var nodeName in _runningNodes.Keys.ToList()) await StopAndUnloadNodeAsync(nodeName);
    }
}
