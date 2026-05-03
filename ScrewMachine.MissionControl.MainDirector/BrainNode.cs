using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using Hexiv.BehaviorTree;
using Hexiv.BehaviorTree.Core;
using Hexiv.BehaviorTree.Builders;

namespace ScrewMachine.MissionControl.MainDirector
{
    [RosNode(DisplayName = "AI 产线总调度大脑", Category = "大脑层 (L1)", Description = "加载 XML 行为树并驱动全厂执行状态")]
    public class BrainNode(INatsClient nats, string nodeName, ILogger<BrainNode> logger)
        : HostedRosNode(nats, nodeName, logger)
    {
        private BehaviorTreeNode? _rootTree;
        private readonly Blackboard _blackboard = new();

        // 全面拥抱原生特性
        [Category("核心配置")]
        [DisplayName("行为树配方路径 (XML)")]
        [Description("要执行的行为树文件绝对路径。留空则执行内置后备逻辑。")]
        [DefaultValue("")]
        [FilePath("XML 行为树配方 (*.xml)|*.xml|所有文件 (*.*)|*.*")]
        public string TreePath { get; set; } = "";

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 从 Parameter Server 同步参数到 C# 属性
            TreePath = Parameters.GetLocal("TreePath", "");

            var factory = new BehaviorTreeFactory();

            // 1. 如果配置了 XML 路径，且文件存在，直接反序列化！
            if (!string.IsNullOrEmpty(TreePath) && File.Exists(TreePath))
            {
                Logger.LogInformation("📂 发现指定的外部行为树配置: {Path}", TreePath);
                try
                {
                    string xmlContent = File.ReadAllText(TreePath);
                    _rootTree = factory.CreateTreeFromXml(xmlContent);
                    Logger.LogInformation("✅ 外部 XML 行为树加载成功！");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "❌ XML 解析失败！");
                    throw; // 触发基类 Faulted 自愈
                }
            }
            // 2. 否则，加载 C# 内部的后备默认树 (Fallback Tree)
            else
            {
                Logger.LogWarning("⚠️ 未指定 XML 路径，或文件不存在，加载内置默认行为树。");

                // 由于我们刚才写了两个漂亮的 BtActionBase 积木，我们可以在 Builder 里优雅地组装它们！
                _rootTree = new BehaviorTreeBuilder()
                    .Sequence("装配测试 (内置)")
                        .Do("视觉定位", async (bb, token) => { await Task.Delay(500); return NodeStatus.Success; })
                        .Selector("智能打紧抉择")
                            .Do("尝试打紧", async (bb, token) => { await Task.Delay(1000); return NodeStatus.Failure; }) // 模拟失败
                            .Do("退钉重试", async (bb, token) => { await Task.Delay(800); return NodeStatus.Success; })
                        .End()
                    .End()
                    .Build();
            }

            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_rootTree == null) return;

            // ==========================================
            // RPC 接口：随时向 Dashboard 提供拓扑结构
            // ==========================================
            var topServer = CreateServer<BtTopologyReq, BtTopologyMsg>("brain.bt.topology.request");
            _ = topServer.ServeAsync(req =>
            {
                var nodes = new List<BtNodeDef>();
                ExtractTopology(_rootTree, nodes);
                return Task.FromResult(new BtTopologyMsg("MainTree", nodes.ToArray()));
            }, stoppingToken);

            // ==========================================
            // 状态机劫持：将内部执行状态化为脑电波广播
            // ==========================================
            var statePub = CreatePublisher<BtStateMsg>("brain.bt.state");
            BehaviorTreeNode.OnNodeTickedHook = (nodeId, status) =>
            {
                var rosStatus = status switch
                {
                    NodeStatus.Success => BtNodeStatus.Success,
                    NodeStatus.Failure => BtNodeStatus.Failure,
                    NodeStatus.Running => BtNodeStatus.Running,
                    _ => BtNodeStatus.Idle
                };
                _ = statePub.PublishAsync(new BtStateMsg("MainTree", new Dictionary<string, BtNodeStatus> { { nodeId, rosStatus } }));
            };

            // ==========================================
            // 核心驱动循环
            // ==========================================
            while (!stoppingToken.IsCancellationRequested)
            {
                Logger.LogInformation("🚀 --------------------------------");
                Logger.LogInformation("🚀 大脑发起新一轮 Mission (生产产品)...");
                _rootTree.Halt();

                // 让大屏先熄灭颜色
                var resetStates = new Dictionary<string, BtNodeStatus>();
                ExtractResetStates(_rootTree, resetStates);
                await statePub.PublishAsync(new BtStateMsg("MainTree", resetStates));
                await Task.Delay(100, stoppingToken);

                NodeStatus treeStatus = NodeStatus.Running;
                while (treeStatus == NodeStatus.Running && !stoppingToken.IsCancellationRequested)
                {
                    treeStatus = await _rootTree.ExecuteTickAsync(_blackboard, stoppingToken);
                    await Task.Delay(50, stoppingToken);
                }

                Logger.LogInformation("✅ 本轮 Mission 结束，等待进站...");
                await Task.Delay(3000, stoppingToken);
            }
        }

        // --- 辅助方法：递归提取拓扑图 ---
        private void ExtractTopology(BehaviorTreeNode node, List<BtNodeDef> list)
        {
            var rosType = node is SequenceNode ? BtNodeType.Sequence :
                          node is SelectorNode ? BtNodeType.Selector :
                          node is RetryNode ? BtNodeType.Condition : BtNodeType.Action;

            var childrenList = node.GetChildren().ToList();
            var idsArray = childrenList.Select(c => c.Id).ToArray();

            list.Add(new BtNodeDef(node.Id, node.Name, rosType, idsArray));

            foreach (var c in childrenList) ExtractTopology(c, list);
        }

        private void ExtractResetStates(BehaviorTreeNode node, Dictionary<string, BtNodeStatus> dict)
        {
            dict[node.Id] = BtNodeStatus.Idle;
            foreach (var c in node.GetChildren()) ExtractResetStates(c, dict);
        }
    }
}