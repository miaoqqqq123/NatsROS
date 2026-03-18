using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NatsROS.Hosting;

namespace NatsROS.Container;

/// <summary>
/// 动态节点管理器：负责通过反射实例化节点，并接管它们的生命周期
/// </summary>
public class DynamicNodeManager(IServiceProvider serviceProvider, ILogger<DynamicNodeManager> logger)
{
    // 存储当前正在运行的节点实例
    private readonly ConcurrentDictionary<string, HostedRosNode> _runningNodes = new();

    public async Task<(bool Success, string Message)> LoadAndStartNodeAsync(string assemblyName, string typeName, string nodeName)
    {
        if (_runningNodes.ContainsKey(nodeName))
            return (false, $"节点名称 '{nodeName}' 已经在运行了！");

        try
        {
            // 1. 通过反射找到目标类型
            var targetType = Type.GetType($"{typeName}, {assemblyName}");
            if (targetType == null)
                return (false, $"找不到类型: {typeName}，请检查 DLL 是否在目录下。");

            if (!typeof(HostedRosNode).IsAssignableFrom(targetType))
                return (false, $"类型 {typeName} 必须继承自 HostedRosNode！");

            // 2. 架构魔法：利用微软 DI 容器动态创建实例，并自动注入 NATS Client 和 Logger！
            var nodeInstance = (HostedRosNode)ActivatorUtilities.CreateInstance(serviceProvider, targetType, nodeName);

            // 3. 手动触发节点的启动生命周期
            await nodeInstance.StartAsync(CancellationToken.None);

            // 4. 登记造册
            _runningNodes[nodeName] = nodeInstance;

            logger.LogInformation("✅ 成功动态加载并运行节点: [{NodeName}] ({TypeName})", nodeName, typeName);
            return (true, "启动成功");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ 动态加载节点 [{NodeName}] 失败！", nodeName);
            return (false, $"启动异常: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> StopAndUnloadNodeAsync(string nodeName)
    {
        if (_runningNodes.TryRemove(nodeName, out var nodeInstance))
        {
            try
            {
                // 手动触发节点的优雅停机
                await nodeInstance.StopAsync(CancellationToken.None);
                logger.LogInformation("⏹️ 成功卸载节点: [{NodeName}]", nodeName);
                return (true, "卸载成功");
            }
            catch (Exception ex)
            {
                return (false, $"停止时发生异常: {ex.Message}");
            }
        }
        return (false, $"未找到名为 '{nodeName}' 的节点。");
    }

    public string[] GetRunningNodes() => _runningNodes.Keys.ToArray();

    // 当母体进程退出时，批量杀死所有子节点
    public async Task ShutdownAllAsync()
    {
        foreach (var nodeName in _runningNodes.Keys)
        {
            await StopAndUnloadNodeAsync(nodeName);
        }
    }
}
