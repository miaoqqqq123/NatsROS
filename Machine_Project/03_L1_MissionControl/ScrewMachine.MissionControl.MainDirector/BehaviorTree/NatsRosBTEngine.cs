using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Core.SystemMessages;

namespace ScrewMachine.MissionControl.MainDirector.BehaviorTree
{
    // ==========================================
    // 核心基类 (带遥测挂载点)
    // ==========================================
    public abstract class BtNode(string name, BtNodeType type)
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; } = name;
        public BtNodeType Type { get; } = type;
        public BtNodeStatus Status { get; protected set; } = BtNodeStatus.Idle;
        public List<BtNode> Children { get; } = new();

        // 核心执行方法
        public abstract Task<BtNodeStatus> TickAsync(Blackboard blackboard, Action reportState, CancellationToken ct);

        public virtual void Reset() { Status = BtNodeStatus.Idle; foreach (var c in Children) c.Reset(); }

        public List<BtNodeDef> ExportTopology()
        {
            var list = new List<BtNodeDef> { new BtNodeDef(Id, Name, Type, Children.Select(c => c.Id).ToArray()) };
            foreach (var c in Children) list.AddRange(c.ExportTopology());
            return list;
        }

        public void ExportState(Dictionary<string, BtNodeStatus> states)
        {
            states[Id] = Status;
            foreach (var c in Children) c.ExportState(states);
        }
    }

    // ==========================================
    // 复合节点 (Composites)
    // ==========================================
    public class SequenceNode(string name) : BtNode(name, BtNodeType.Sequence)
    {
        public override async Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = BtNodeStatus.Running; report();
            foreach (var child in Children)
            {
                var s = await child.TickAsync(bb, report, ct);
                if (s == BtNodeStatus.Failure) { Status = BtNodeStatus.Failure; report(); return Status; }
            }
            Status = BtNodeStatus.Success; report(); return Status;
        }
    }

    public class SelectorNode(string name) : BtNode(name, BtNodeType.Selector)
    {
        public override async Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = BtNodeStatus.Running; report();
            foreach (var child in Children)
            {
                var s = await child.TickAsync(bb, report, ct);
                if (s == BtNodeStatus.Success) { Status = BtNodeStatus.Success; report(); return Status; }
            }
            Status = BtNodeStatus.Failure; report(); return Status;
        }
    }

    // ==========================================
    // 装饰器节点 (Decorators) - 工业场景极其常用！
    // ==========================================

    // 重试器：失败了自动重试 N 次
    public class RetryNode(string name, int maxRetries) : BtNode(name, BtNodeType.Condition)
    {
        public override async Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = BtNodeStatus.Running; report();
            var child = Children.FirstOrDefault();
            if (child == null) return BtNodeStatus.Failure;

            for (int i = 0; i < maxRetries; i++)
            {
                var s = await child.TickAsync(bb, report, ct);
                if (s == BtNodeStatus.Success) { Status = BtNodeStatus.Success; report(); return Status; }
                child.Reset(); // 失败了，重置子节点状态准备下一次重试
            }
            Status = BtNodeStatus.Failure; report(); return Status;
        }
    }

    // 反转器：把 Success 变 Failure，Failure 变 Success
    public class InverterNode(string name) : BtNode(name, BtNodeType.Condition)
    {
        public override async Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = BtNodeStatus.Running; report();
            var child = Children.FirstOrDefault();
            if (child == null) return BtNodeStatus.Failure;

            var s = await child.TickAsync(bb, report, ct);
            Status = s == BtNodeStatus.Success ? BtNodeStatus.Failure : (s == BtNodeStatus.Failure ? BtNodeStatus.Success : s);
            report(); return Status;
        }
    }

    // ==========================================
    // 叶子节点 (Leaves)
    // ==========================================
    public class ActionLeaf(string name, Func<Blackboard, CancellationToken, Task<bool>> action) : BtNode(name, BtNodeType.Action)
    {
        public override async Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = BtNodeStatus.Running; report();
            try { Status = await action(bb, ct) ? BtNodeStatus.Success : BtNodeStatus.Failure; }
            catch { Status = BtNodeStatus.Failure; }
            report(); return Status;
        }
    }

    public class ConditionLeaf(string name, Func<Blackboard, bool> condition) : BtNode(name, BtNodeType.Condition)
    {
        public override Task<BtNodeStatus> TickAsync(Blackboard bb, Action report, CancellationToken ct)
        {
            Status = condition(bb) ? BtNodeStatus.Success : BtNodeStatus.Failure;
            report(); return Task.FromResult(Status);
        }
    }
}