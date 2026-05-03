using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexiv.BehaviorTree.Core;

namespace Hexiv.BehaviorTree.Builders
{
    public class BehaviorTreeBuilder
    {
        private readonly Stack<BehaviorTreeNode> _nodeStack = new();
        private BehaviorTreeNode? _root;

        public BehaviorTreeBuilder Sequence(string name) => PushComposite(new SequenceNode(name));
        public BehaviorTreeBuilder Selector(string name) => PushComposite(new SelectorNode(name));
        public BehaviorTreeBuilder Fallback(string name) => PushComposite(new FallbackNode(name));

        public BehaviorTreeBuilder Retry(string name, int maxRetries) => PushDecorator(new RetryNode(name, maxRetries));

        public BehaviorTreeBuilder Do(string name, Func<Blackboard, CancellationToken, Task<NodeStatus>> actionFunc)
            => AddLeaf(new ActionNode(name, actionFunc));

        public BehaviorTreeBuilder End()
        {
            if (_nodeStack.Count > 0) _nodeStack.Pop();
            return this;
        }

        public BehaviorTreeNode Build()
        {
            if (_root == null) throw new InvalidOperationException("行为树为空，无法构建！");
            return _root;
        }

        private BehaviorTreeBuilder PushComposite(ControlNode node)
        {
            if (_root == null) _root = node;
            else AddChildToCurrent(node);
            _nodeStack.Push(node);
            return this;
        }

        private BehaviorTreeBuilder PushDecorator(RetryNode node)
        {
            if (_root == null) _root = node;
            else AddChildToCurrent(node);
            _nodeStack.Push(node);
            return this;
        }

        private BehaviorTreeBuilder AddLeaf(BehaviorTreeNode node)
        {
            if (_nodeStack.Count == 0) throw new InvalidOperationException("叶子节点必须放在复合节点或装饰器内部！");
            AddChildToCurrent(node);
            return this;
        }

        private void AddChildToCurrent(BehaviorTreeNode node)
        {
            var parent = _nodeStack.Peek();
            if (parent is ControlNode controlNode) controlNode.AddChild(node);
            else if (parent is RetryNode retryNode) retryNode.SetChild(node);
            else throw new InvalidOperationException($"节点 [{parent.Name}] 不支持添加子节点！");
        }
    }
}