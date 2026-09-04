using Hexiv.BehaviorTree;
using Hexiv.BehaviorTree.Builders;
using Hexiv.BehaviorTree.Core;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NatsROS.Core.Attributes;
using NatsROS.Core.Communication;
using NatsROS.Core.SystemMessages;
using NatsROS.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScrewMachine.MissionControl.MainDirector
{
    [RosNode(DisplayName = "AI 产线总调度大脑", Category = "大脑层 (L1)", Description = "加载 XML 行为树并驱动全厂执行状态")]
    public class BrainNode : HostedRosNode
    {
        // 全面拥抱原生特性
        [Category("核心配置")]
        [DisplayName("行为树配方路径 (XML)")]
        [Description("要执行的行为树文件绝对路径。留空则执行内置后备逻辑。")]
        [DefaultValue("")]
        [FilePath("XML 行为树配方 (*.xml)|*.xml|所有文件 (*.*)|*.*")]
        public string TreePath { get; set; } = "";

        [Category("核心配置")]
        [DisplayName("开机自动执行")]
        [Description("如果为 False，则需要等待 Dashboard 或上位机发送 Start 指令才能运行")]
        [DefaultValue("True")]
        public bool AutoStart { get; set; } = false;

        private BehaviorTreeNode? _rootNode;
        private Blackboard _blackboard = new();
        private CancellationTokenSource? _btCts;
        private Task? _btExecutionTask;

        // 状态遥测相关
        private RosPublisher<BtStateMsg>? _statePublisher;
        private readonly ConcurrentDictionary<string, NatsROS.Core.SystemMessages.BtNodeStatus> _nodeStates = new();
        private bool _stateDirty = false;

        public BrainNode(INatsClient nats, string nodeName, ILogger<BrainNode> logger) : base(nats, nodeName, logger)
        {

        }

        protected override Task OnConfigureAsync(CancellationToken ct)
        {
            // 1. 从 Parameter Server 获取参数
            TreePath = Parameters.GetLocal("TreePath", "MainTree.xml");

            // 【修复】：恢复 AutoStart 的读取，如果没配默认给 False（等 HMI 发指令才启动）
            AutoStart = bool.Parse(Parameters.GetLocal("AutoStart", "False"));

            _statePublisher = CreatePublisher<BtStateMsg>($"{Name}.bt.state", RosQosProfile.SensorData);

            if (!string.IsNullOrEmpty(TreePath))
            {
                // ==========================================
                // 【核心修复】：使用全局路径大管家，去当前工作区的 BehaviorTrees 目录下找！
                // ==========================================
                string btDir = NatsROS.Core.Environment.WorkspaceManager.GetBehaviorTreesPath();

                // 兼容性处理：如果传进来的是绝对路径就直接用，否则拼接标准工作区路径
                string fullPath = Path.IsPathRooted(TreePath) ? TreePath : Path.Combine(btDir, TreePath);

                if (File.Exists(fullPath))
                {
                    Logger.LogInformation("🧠 正在从当前工程环境加载行为树: {Path}", fullPath);
                    LoadTreeFromXml(File.ReadAllText(fullPath));
                }
                else
                {
                    Logger.LogWarning("⚠️ 未找到行为树剧本文件: {Path}。大脑进入空载待命模式。", fullPath);
                }
            }


            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInformation("🧠 大脑节点已激活！AutoStart={AutoStart}", AutoStart);

            // ==========================================
            // 1. 注册核心控制服务 (RPC Servers)
            // ==========================================
            var reloadServer = CreateServer<ReloadTreeReq, ReloadTreeRes>($"{Name}.bt.reload");
            _ = reloadServer.ServeAsync(async req =>
            {
                Logger.LogWarning("♻️ 收到网络热重载请求！正在熔断当前行为树...");
                await StopTreeAsync();

                try
                {
                    LoadTreeFromXml(req.XmlContent);
                    Logger.LogInformation("✅ 行为树热重载成功！");

                    if (AutoStart) _ = StartTreeAsync(); // 如果配置了自动启动，重载后立即发车！
                    return new ReloadTreeRes(true, "重载并解析成功");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "❌ 热重载失败，XML 语法或反射错误。");
                    return new ReloadTreeRes(false, ex.Message);
                }
            }, stoppingToken);

            var startServer = CreateServer<StartTreeReq, StartTreeRes>($"{Name}.bt.start");
            _ = startServer.ServeAsync(async req =>
            {
                if (_rootNode == null) return new StartTreeRes(false, "大脑中没有加载行为树配方");
                await StartTreeAsync(req.IsLoop, req.ContextData);
                return new StartTreeRes(true, "行为树已启动");
            }, stoppingToken);

            var stopServer = CreateServer<StopTreeReq, StopTreeRes>($"{Name}.bt.stop");
            _ = stopServer.ServeAsync(async req =>
            {
                await StopTreeAsync();
                return new StopTreeRes(true, "行为树已停止");
            }, stoppingToken);

            // ==========================================
            // 2. 注册拓扑图请求服务 (供 Dashboard 画图)
            // ==========================================
            var topServer = CreateServer<BtTopologyReq, BtTopologyMsg>($"{Name}.bt.topology.request");
            _ = topServer.ServeAsync(req =>
            {
                var defs = new List<BtNodeDef>();
                if (_rootNode != null) ExtractTopology(_rootNode, defs);
                return Task.FromResult(new BtTopologyMsg(Name, defs.ToArray()));
            }, stoppingToken);

            // ==========================================
            // 3. 启动状态广播循环 (10Hz 节流，防网络风暴)
            // ==========================================
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (_stateDirty && _statePublisher != null)
                    {
                        _stateDirty = false;
                        var payload = new Dictionary<string, NatsROS.Core.SystemMessages.BtNodeStatus>(_nodeStates);
                        await _statePublisher.PublishAsync(new BtStateMsg(Name, payload), stoppingToken);
                    }
                    await Task.Delay(100, stoppingToken);
                }
            }, stoppingToken);

            // 4. 根据 AutoStart 决定是否开机直接干活
            if (AutoStart && _rootNode != null)
            {
                _ = StartTreeAsync(true);
            }

            var pauseServer = CreateServer<PauseTreeReq, PauseTreeRes>($"{Name}.bt.pause");
            _ = pauseServer.ServeAsync(async req =>
            {
                await PauseTreeAsync();
                return new PauseTreeRes(true, "系统已挂起");
            }, stoppingToken);

            var resumeServer = CreateServer<ResumeTreeReq, ResumeTreeRes>($"{Name}.bt.resume");
            _ = resumeServer.ServeAsync(req =>
            {
                if (_rootNode == null) return Task.FromResult(new ResumeTreeRes(false, "空配方"));
                ResumeTreeAsync(loop: false);
                return Task.FromResult(new ResumeTreeRes(true, "从断点继续执行"));
            }, stoppingToken);

            // 挂起主循环，维持节点存活
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        // 2. 将底部的控制逻辑，完全替换为以下四个方法：
        private async Task StartTreeAsync(bool loop = false, Dictionary<string, string>? context = null)
        {
            await StopTreeAsync(); // 彻底复位
            _blackboard.Set("Nats", Nats);
            _blackboard.Set("IsPaused", false);

            if (context != null) foreach (var kv in context) _blackboard.Set(kv.Key, kv.Value);

            StartExecutionLoop(loop);
        }

        private async Task StopTreeAsync()
        {
            if (_btCts != null)
            {
                _blackboard.Set("IsPaused", false); // 这代表彻底急停 (Abort)
                _btCts.Cancel();
                if (_btExecutionTask != null) await Task.WhenAny(_btExecutionTask, Task.Delay(2000));
                _btCts.Dispose();
                _btCts = null;
            }
            if (_rootNode != null) _rootNode.Halt(); // 清空所有节点的执行指针！
        }

        private async Task PauseTreeAsync()
        {
            if (_btCts != null && !_btCts.IsCancellationRequested)
            {
                Logger.LogWarning("⏸️ 收到暂停指令，正在冻结行为树内存指针...");
                _blackboard.Set("IsPaused", true); // 打上挂起标记
                _btCts.Cancel(); // 瞬间刹停底层所有正在执行的马达和阀门

                if (_btExecutionTask != null) await Task.WhenAny(_btExecutionTask, Task.Delay(2000));
            }
        }

        private void ResumeTreeAsync(bool loop)
        {
            Logger.LogInformation("▶️ 收到恢复指令，从断点继续执行...");
            _blackboard.Set("IsPaused", false); // 解除挂起标记
            // 极其关键：这里绝对不能调用 _rootNode.Halt()！
            StartExecutionLoop(loop);
        }

        private void StartExecutionLoop(bool loop)
        {
            _btCts = new CancellationTokenSource();
            _btExecutionTask = Task.Run(async () =>
            {
                try
                {
                    while (!_btCts.Token.IsCancellationRequested && _rootNode != null)
                    {
                        var status = await _rootNode.ExecuteTickAsync(_blackboard, _btCts.Token);
                        if (status == BtNodeStatus.Success || status == BtNodeStatus.Failure)
                        {
                            _rootNode.Halt();
                            var isPass = status == BtNodeStatus.Success;
                            await Nats.PublishAsync($"{Name}.bt.result", new BtResultMsg(isPass));
                            if (!loop) break;
                            await Task.Delay(1000, _btCts.Token);
                        }
                        else
                        {
                            await Task.Delay(10, _btCts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) { /* 取消跳出循环是正常逻辑 */ }
                catch (Exception ex) { Logger.LogError(ex, "行为树执行崩溃！"); }
            });
        }

        // ==========================================
        // 核心调度控制引擎
        // ==========================================
        private void LoadTreeFromXml(string xmlContent)
        {
            var factory = new BehaviorTreeFactory();
            _rootNode = factory.CreateTreeFromXml(xmlContent);
            _blackboard = new Blackboard(); // 清空旧数据
            _nodeStates.Clear();

            //为这棵树里的每一个节点，注入当前大脑专属的字典更新方法！
            _rootNode.SetReporter((id, status) =>
            {
                _nodeStates[id] = MapStatus(status);
                _stateDirty = true;
            });

            _stateDirty = true;
        }

        // ==========================================
        // 辅助映射方法
        // ==========================================
        private void ExtractTopology(BehaviorTreeNode node, List<BtNodeDef> defs)
        {
            var childrenIds = node.GetChildren().Select(c => c.Id).ToArray();
            defs.Add(new BtNodeDef(node.Id, node.Name, MapType(node.NodeType), childrenIds));
            foreach (var child in node.GetChildren()) ExtractTopology(child, defs);
        }

        private NatsROS.Core.SystemMessages.BtNodeType MapType(BtNodeType type) => type switch
        {
            BtNodeType.Sequence => NatsROS.Core.SystemMessages.BtNodeType.Sequence,
            BtNodeType.Selector => NatsROS.Core.SystemMessages.BtNodeType.Selector,
            BtNodeType.Action => NatsROS.Core.SystemMessages.BtNodeType.Action,
            BtNodeType.Condition => NatsROS.Core.SystemMessages.BtNodeType.Condition,
            BtNodeType.Decorator => NatsROS.Core.SystemMessages.BtNodeType.Decorator, // 视觉上将装饰器归类为菱形条件
            BtNodeType.Root => NatsROS.Core.SystemMessages.BtNodeType.Sequence,       // 视觉上将 Root 归类为方块
            _ => NatsROS.Core.SystemMessages.BtNodeType.Sequence
        };

        private NatsROS.Core.SystemMessages.BtNodeStatus MapStatus(BtNodeStatus status) => status switch
        {
            BtNodeStatus.Idle => NatsROS.Core.SystemMessages.BtNodeStatus.Idle,
            BtNodeStatus.Running => NatsROS.Core.SystemMessages.BtNodeStatus.Running,
            BtNodeStatus.Success => NatsROS.Core.SystemMessages.BtNodeStatus.Success,
            BtNodeStatus.Failure => NatsROS.Core.SystemMessages.BtNodeStatus.Failure,
            _ => NatsROS.Core.SystemMessages.BtNodeStatus.Idle
        };
    }
}