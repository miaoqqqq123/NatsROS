using System.ComponentModel;
using NatsROS.BehaviorTree.Attributes;
using NatsROS.BehaviorTree.Builders;
using NatsROS.Core.Attributes;
using NatsROS.Core.SystemMessages;

namespace NatsROS.BehaviorTree.Core
{
    // ==========================================
    // 积木：执行子树 (Sub-Tree)
    // ==========================================
    [BtNode(DisplayName = "执行子树 (Sub-Tree)", Category = "流程控制", Description = "无缝嵌入并执行外部的 XML 行为树配方")]
    public class SubTreeAction : BehaviorTreeNode
    {
        private string _targetXmlPath = "";
        private BehaviorTreeNode? _subTreeRoot;

        [Category("子树配置")]
        [DisplayName("剧本文件相对路径")]
        [Description("指定 Recipes 文件夹下的 XML 文件名，如 'Homing.xml'")]
        [DefaultValue("SubTree.xml")]
        [FilePath("XML Files (*.xml)|*.xml")]
        public string TargetXmlPath
        {
            get => _targetXmlPath;
            set
            {
                _targetXmlPath = value;
                // 当工艺员在右侧属性栏填入路径时，瞬间在后台解析并验证该树！
                LoadSubTree();
            }
        }

        // 在 UI 拓扑图中，我们让它伪装成一个 Sequence（方块），以表示它内部包裹着一堆东西
        public SubTreeAction(string name) : base(name, BtNodeType.Sequence)
        {
        }

        // ==========================================
        // 核心一：动态装载与 ID 防火墙
        // ==========================================
        private void LoadSubTree()
        {
            if (string.IsNullOrWhiteSpace(_targetXmlPath)) return;

            // 智能路径推导：强制从统一部署目录下的 Recipes 文件夹寻找
            string recipeDir = NatsROS.Core.Environment.WorkspaceManager.GetBehaviorTreesPath();
            string fullPath = Path.IsPathRooted(_targetXmlPath) ? _targetXmlPath : Path.Combine(recipeDir, _targetXmlPath);

            if (File.Exists(fullPath))
            {
                try
                {
                    // 1. 读取 XML 并生成子树实例
                    var factory = new BehaviorTreeFactory();
                    _subTreeRoot = factory.CreateTreeFromXml(File.ReadAllText(fullPath));

                    // 2. 【黑魔法】：动态重写所有子节点的 ID！
                    // 为什么？因为如果一棵主树里引用了两次同一个 Homing.xml，它们的节点 ID 会完全一致！
                    // 这会导致大脑的 _nodeStates 字典冲突。加上随机前缀完美解决。
                    string prefix = Guid.NewGuid().ToString("N").Substring(0, 5);
                    RegenerateIds(_subTreeRoot, prefix);

                    // 如果当前节点已经有了汇报器，立刻传给刚出生的子树！
                    if (this.Reporter != null)
                    {
                        _subTreeRoot.SetReporter(this.Reporter);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"子树 [{_targetXmlPath}] 加载失败: {ex.Message}");
                }
            }
        }

        private void RegenerateIds(BehaviorTreeNode node, string prefix)
        {
            node.Id = $"{prefix}_{node.Id}";
            foreach (var child in node.GetChildren()) RegenerateIds(child, prefix);
        }

        // ==========================================
        // 核心二：向大屏监控系统“交底”
        // ==========================================
        public override IEnumerable<BehaviorTreeNode> GetChildren()
        {
            // 极其优雅的递归拓展！当 BrainNode 提取拓扑图时，
            // 这个方法会把隐藏在内部的子树根节点暴露出去，实现 UI 的无缝拼接！
            if (_subTreeRoot != null) yield return _subTreeRoot;
        }

        // ==========================================
        // 核心三：黑板穿透执行
        // ==========================================
        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            if (_subTreeRoot == null)
            {
                LoadSubTree(); // 兜底：运行时最后尝试加载一次
                if (_subTreeRoot == null) return BtNodeStatus.Failure;
            }

            // 直接调用子树的 ExecuteTickAsync，并将当前的主黑板 (Blackboard) 完美传进去！
            // 这样子树里面就能随意使用主树建立的 NATS 客户端、条码、偏差值了。
            return await _subTreeRoot.ExecuteTickAsync(blackboard, ct);
        }

        public override void Halt()
        {
            base.Halt();
            _subTreeRoot?.Halt(); // 复位主树时，把子树的状态灯也全部熄灭
        }


        public override void SetReporter(Action<string, BtNodeStatus> reporter)
        {
            base.SetReporter(reporter);
            _subTreeRoot?.SetReporter(reporter);
        }
    }
}