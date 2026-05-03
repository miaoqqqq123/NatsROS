using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hexiv.BehaviorTree.Core
{
    // ==========================================
    // 0. 任务根节点 (Root Node) - 对齐 Dashboard
    // ==========================================
    public class RootNode : ControlNode
    {
        public RootNode(string name = "任务起点") : base(name, BTNodeType.Root) { }

        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            // Root 节点通常只有一个子节点（主干 Sequence）
            if (Children.Count > 0)
            {
                return await Children[0].ExecuteTickAsync(blackboard, ct);
            }
            return NodeStatus.Success;
        }
    }

    // ==========================================
    // 5. 插件化动作节点基类 (相当于 OpenTAP 的 TestStep)
    // 业务开发者应该继承此类来编写具体的动作逻辑！
    // ==========================================
    public abstract class BtActionBase : BehaviorTreeNode
    {
        protected BtActionBase(string name) : base(name, BTNodeType.Action) { }

        // 封死原有的 OnTickAsync，强制进行异常捕获包装
        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try
            {
                return await OnExecuteAsync(blackboard, ct);
            }
            catch (OperationCanceledException)
            {
                return NodeStatus.Failure;
            }
            catch (Exception)
            {
                return NodeStatus.Failure;
            }
        }

        // 【核心】：业务开发者只需重写这个方法！
        protected abstract Task<NodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct);
    }

    // ==========================================
    // 6. 插件化条件节点基类
    // ==========================================
    public abstract class BtConditionBase : BehaviorTreeNode
    {
        protected BtConditionBase(string name) : base(name, BTNodeType.Condition) { }

        protected override Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try
            {
                bool result = OnCheck(blackboard);
                return Task.FromResult(result ? NodeStatus.Success : NodeStatus.Failure);
            }
            catch
            {
                return Task.FromResult(NodeStatus.Failure);
            }
        }

        // 【核心】：业务开发者只需重写这个同步方法，返回 true/false！
        protected abstract bool OnCheck(Blackboard blackboard);
    }

    public abstract class ControlNode : BehaviorTreeNode
    {
        public List<BehaviorTreeNode> Children { get; } = new();
        protected int CurrentChildIndex = 0;

        protected ControlNode(string name, BTNodeType type) : base(name, type) { }

        public void AddChild(BehaviorTreeNode child) => Children.Add(child);

        public override IEnumerable<BehaviorTreeNode> GetChildren() => Children;

        public override void Halt()
        {
            CurrentChildIndex = 0;
            base.Halt();
        }
    }

    public class SequenceNode : ControlNode
    {
        public SequenceNode(string name = "Sequence") : base(name, BTNodeType.Sequence) { }

        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            while (CurrentChildIndex < Children.Count)
            {
                var child = Children[CurrentChildIndex];
                var status = await child.ExecuteTickAsync(blackboard, ct);

                if (status == NodeStatus.Running) return NodeStatus.Running;
                if (status == NodeStatus.Failure)
                {
                    CurrentChildIndex = 0;
                    return NodeStatus.Failure;
                }
                CurrentChildIndex++;
            }
            CurrentChildIndex = 0;
            return NodeStatus.Success;
        }
    }

    public class SelectorNode : ControlNode
    {
        public SelectorNode(string name = "Selector") : base(name, BTNodeType.Selector) { }

        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            while (CurrentChildIndex < Children.Count)
            {
                var child = Children[CurrentChildIndex];
                var status = await child.ExecuteTickAsync(blackboard, ct);

                if (status == NodeStatus.Running) return NodeStatus.Running;
                if (status == NodeStatus.Success)
                {
                    CurrentChildIndex = 0;
                    return NodeStatus.Success;
                }
                CurrentChildIndex++;
            }
            CurrentChildIndex = 0;
            return NodeStatus.Failure;
        }
    }

    // 兼容原版的别名
    public class FallbackNode : SelectorNode { public FallbackNode(string name = "Fallback") : base(name) { } }

    public class ActionNode : BehaviorTreeNode
    {
        private readonly Func<Blackboard, CancellationToken, Task<NodeStatus>> _actionFunc;

        public ActionNode(string name, Func<Blackboard, CancellationToken, Task<NodeStatus>> actionFunc)
            : base(name, BTNodeType.Action) => _actionFunc = actionFunc;

        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try { return await _actionFunc(blackboard, ct); }
            catch { return NodeStatus.Failure; }
        }
    }

    public class RetryNode : BehaviorTreeNode
    {
        private BehaviorTreeNode? _child;
        private readonly int _maxRetries;

        public RetryNode(string name, int maxRetries) : base(name, BTNodeType.Decorator)
        {
            _maxRetries = maxRetries;
        }

        public void SetChild(BehaviorTreeNode child) => _child = child;

        public override IEnumerable<BehaviorTreeNode> GetChildren()
        {
            if (_child != null) yield return _child;
        }

        protected override async Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            if (_child == null) return NodeStatus.Failure;

            for (int i = 0; i < _maxRetries; i++)
            {
                var status = await _child.ExecuteTickAsync(blackboard, ct);
                if (status == NodeStatus.Success || status == NodeStatus.Running) return status;

                _child.Halt(); // 失败了，重置状态准备下一次重试
            }
            return NodeStatus.Failure;
        }
    }
}