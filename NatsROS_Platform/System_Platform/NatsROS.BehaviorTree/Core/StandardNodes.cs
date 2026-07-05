using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Core.SystemMessages;

namespace Hexiv.BehaviorTree.Core
{
    // ==========================================
    // 0. 任务根节点 (Root Node) - 对齐 Dashboard
    // ==========================================
    public class RootNode : ControlNode
    {
        public RootNode(string name = "任务起点") : base(name, BtNodeType.Root) { }

        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            // Root 节点通常只有一个子节点（主干 Sequence）
            if (Children.Count > 0)
            {
                return await Children[0].ExecuteTickAsync(blackboard, ct);
            }
            return BtNodeStatus.Success;
        }
    }

    // ==========================================
    // 5. 插件化动作节点基类 (相当于 OpenTAP 的 TestStep)
    // 业务开发者应该继承此类来编写具体的动作逻辑！
    // ==========================================
    public abstract class BtActionBase : BehaviorTreeNode
    {
        protected BtActionBase(string name) : base(name, BtNodeType.Action) { }

        // 封死原有的 OnTickAsync，强制进行异常捕获包装
        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try
            {
                return await OnExecuteAsync(blackboard, ct);
            }
            catch (OperationCanceledException)
            {
                // 【绝妙的黑魔法】：区分“彻底终止”与“人工暂停”
                // 如果是暂停，我们返回 Running 状态，这样父节点(Sequence)的当前进度指针就不会被清零！
                if (blackboard.Get("IsPaused", out bool isPaused) && isPaused)
                {
                    return BtNodeStatus.Running; // 内存冻结！
                }

                return BtNodeStatus.Failure; // 彻底急停/报错
            }
            catch (Exception)
            {
                return BtNodeStatus.Failure;
            }
        }

        // 【核心】：业务开发者只需重写这个方法！
        protected abstract Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct);
    }

    // ==========================================
    // 6. 插件化条件节点基类
    // ==========================================
    public abstract class BtConditionBase : BehaviorTreeNode
    {
        protected BtConditionBase(string name) : base(name, BtNodeType.Condition) { }

        protected override Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try
            {
                bool result = OnCheck(blackboard);
                return Task.FromResult(result ? BtNodeStatus.Success : BtNodeStatus.Failure);
            }
            catch
            {
                return Task.FromResult(BtNodeStatus.Failure);
            }
        }

        // 【核心】：业务开发者只需重写这个同步方法，返回 true/false！
        protected abstract bool OnCheck(Blackboard blackboard);
    }

    public abstract class ControlNode : BehaviorTreeNode
    {
        public List<BehaviorTreeNode> Children { get; } = new();
        protected int CurrentChildIndex = 0;

        protected ControlNode(string name, BtNodeType type) : base(name, type) { }

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
        public SequenceNode(string name = "Sequence") : base(name, BtNodeType.Sequence) { }

        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            while (CurrentChildIndex < Children.Count)
            {
                var child = Children[CurrentChildIndex];
                var status = await child.ExecuteTickAsync(blackboard, ct);

                if (status == BtNodeStatus.Running) return BtNodeStatus.Running;
                if (status == BtNodeStatus.Failure)
                {
                    CurrentChildIndex = 0;
                    return BtNodeStatus.Failure;
                }
                CurrentChildIndex++;
            }
            CurrentChildIndex = 0;
            return BtNodeStatus.Success;
        }
    }

    public class SelectorNode : ControlNode
    {
        public SelectorNode(string name = "Selector") : base(name, BtNodeType.Selector) { }

        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            while (CurrentChildIndex < Children.Count)
            {
                var child = Children[CurrentChildIndex];
                var status = await child.ExecuteTickAsync(blackboard, ct);

                if (status == BtNodeStatus.Running) return BtNodeStatus.Running;
                if (status == BtNodeStatus.Success)
                {
                    CurrentChildIndex = 0;
                    return BtNodeStatus.Success;
                }
                CurrentChildIndex++;
            }
            CurrentChildIndex = 0;
            return BtNodeStatus.Failure;
        }
    }

    // 兼容原版的别名
    public class FallbackNode : SelectorNode { public FallbackNode(string name = "Fallback") : base(name) { } }

    public class ActionNode : BehaviorTreeNode
    {
        private readonly Func<Blackboard, CancellationToken, Task<BtNodeStatus>> _actionFunc;

        public ActionNode(string name, Func<Blackboard, CancellationToken, Task<BtNodeStatus>> actionFunc)
            : base(name, BtNodeType.Action) => _actionFunc = actionFunc;

        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            try { return await _actionFunc(blackboard, ct); }
            catch { return BtNodeStatus.Failure; }
        }
    }

    public class RetryNode : BehaviorTreeNode
    {
        private BehaviorTreeNode? _child;

        [System.ComponentModel.Category("执行逻辑")]
        [System.ComponentModel.DisplayName("最大重试次数")]
        [System.ComponentModel.Description("当子节点返回 Failure 时，最大允许重复执行的次数。")]
        [System.ComponentModel.DefaultValue(3)]
        public int MaxRetries { get; set; } = 3;

        public RetryNode(string name) : base(name, BtNodeType.Decorator)
        {

        }

        public void SetChild(BehaviorTreeNode child) => _child = child;

        public override IEnumerable<BehaviorTreeNode> GetChildren()
        {
            if (_child != null) yield return _child;
        }

        protected override async Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            if (_child == null) return BtNodeStatus.Failure;

            for (int i = 0; i < MaxRetries; i++)
            {
                var status = await _child.ExecuteTickAsync(blackboard, ct);
                if (status == BtNodeStatus.Success || status == BtNodeStatus.Running) return status;

                _child.Halt(); // 失败了，重置状态准备下一次重试
            }
            return BtNodeStatus.Failure;
        }
    }
}