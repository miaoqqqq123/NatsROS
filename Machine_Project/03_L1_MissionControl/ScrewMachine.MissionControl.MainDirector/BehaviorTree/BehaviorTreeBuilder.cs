using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NatsROS.Core.SystemMessages;

namespace ScrewMachine.MissionControl.MainDirector.BehaviorTree
{
    // ==========================================
    // Fluent 链式行为树构建器
    // ==========================================
    public class BehaviorTreeBuilder
    {
        private readonly Stack<BtNode> _nodeStack = new();
        private BtNode? _root;

        public BehaviorTreeBuilder Sequence(string name) => AddComposite(new SequenceNode(name));
        public BehaviorTreeBuilder Selector(string name) => AddComposite(new SelectorNode(name));

        public BehaviorTreeBuilder Retry(string name, int times) => AddComposite(new RetryNode(name, times));
        public BehaviorTreeBuilder Invert(string name) => AddComposite(new InverterNode(name));

        public BehaviorTreeBuilder Action(string name, Func<Blackboard, CancellationToken, Task<bool>> action) => AddLeaf(new ActionLeaf(name, action));
        public BehaviorTreeBuilder Condition(string name, Func<Blackboard, bool> condition) => AddLeaf(new ConditionLeaf(name, condition));

        // 封口操作，退回上一层级
        public BehaviorTreeBuilder End()
        {
            if (_nodeStack.Count > 1) _nodeStack.Pop();
            return this;
        }

        public BtNode Build() => _root ?? throw new InvalidOperationException("树是空的！");

        private BehaviorTreeBuilder AddComposite(BtNode node)
        {
            if (_root == null) _root = node;
            else _nodeStack.Peek().Children.Add(node);

            _nodeStack.Push(node);
            return this;
        }

        private BehaviorTreeBuilder AddLeaf(BtNode node)
        {
            if (_nodeStack.Count == 0) throw new InvalidOperationException("叶子节点必须放在复合节点内部！");
            _nodeStack.Peek().Children.Add(node);
            return this;
        }
    }
}