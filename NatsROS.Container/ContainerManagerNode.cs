using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Net;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;

namespace NatsROS.Container;

/// <summary>
/// 母体管理节点：对外暴露 RPC 接口
/// </summary>
public class ContainerManagerNode(INatsClient nats, ILogger<ContainerManagerNode> logger, DynamicNodeManager nodeManager)
    : HostedRosNode(nats, "container_manager", logger)
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogWarning("🛡️ NatsROS 母体引擎 [Container] 已启动！正在监听控制指令...");

        var loadServer = CreateServer<LoadNodeReq, LoadNodeRes>("container.load_node");
        var unloadServer = CreateServer<UnloadNodeReq, UnloadNodeRes>("container.unload_node");
        var listServer = CreateServer<ListNodesReq, ListNodesRes>("container.list_nodes");

        // 并发处理各种管理请求
        var t1 = loadServer.ServeAsync(async req =>
        {
            Logger.LogInformation("收到加载节点请求: {TypeName} -> {NodeName}", req.TypeName, req.NodeName);
            var result = await nodeManager.LoadNodeFromReqAsync(req);
            return new LoadNodeRes(result.Success, result.Message);
        }, stoppingToken);

        var t2 = unloadServer.ServeAsync(async req =>
        {
            Logger.LogInformation("收到卸载节点请求: {NodeName}", req.NodeName);
            var result = await nodeManager.StopAndUnloadNodeAsync(req.NodeName);
            return new UnloadNodeRes(result.Success, result.Message);
        }, stoppingToken);

        var t3 = listServer.ServeAsync(req =>
        {
            var nodes = nodeManager.GetRunningNodes();
            return Task.FromResult(new ListNodesRes(nodes));
        }, stoppingToken);

        // 追加状态切换监听
        var stateServer = CreateServer<ChangeStateReq, ChangeStateRes>("container.change_state");
        var t4 = stateServer.ServeAsync(async req =>
        {
            Logger.LogInformation("收到状态切换请求: {Node} -> {State}", req.NodeName, req.TargetState);
            var result = await nodeManager.ChangeNodeStateAsync(req.NodeName, req.TargetState);
            return new ChangeStateRes(result.Success);
        }, stoppingToken);

        await Task.WhenAll(t1, t2, t3, t4); // 记得把 t4 加进等待里
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogWarning("🛡️ 母体正在关闭，准备通知所有子节点下线...");
        await nodeManager.ShutdownAllAsync();
        await base.StopAsync(cancellationToken);
    }
}
