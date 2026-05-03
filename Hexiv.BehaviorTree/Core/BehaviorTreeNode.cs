using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Hexiv.BehaviorTree.Core
{
    public abstract class BehaviorTreeNode
    {
        [Category("1. 基本信息")]
        [DisplayName("节点唯一 ID (Id)")]
        [Description("系统自动生成的全网唯一标识符，确保拓扑图跟踪的绝对准确。")]
        [ReadOnly(true)]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Category("1. 基本信息")]
        [DisplayName("节点显示名称 (Name)")]
        [Description("在图纸上显示的友好名称，建议修改为易懂的业务动作。")]
        public string Name { get; set; }

        [Browsable(false)]
        public BTNodeType NodeType { get; protected set; }

        private NodeStatus _status = NodeStatus.Idle;
        public NodeStatus Status
        {
            get => _status;
            protected set
            {
                if (_status != value)
                {
                    _status = value;
                    // 【高能核心】：状态发生改变时，触发全局拦截器！
                    OnNodeTickedHook?.Invoke(this.Id, _status);
                }
            }
        }

        // 静态全局钩子：极其优雅的解耦方式！引擎本身不需要依赖 NATS。
        public static Action<string, NodeStatus>? OnNodeTickedHook;

        protected BehaviorTreeNode(string name, BTNodeType type)
        {
            Name = name;
            NodeType = type;
        }

        // 允许外部遍历树结构 (用于画拓扑图)
        public virtual IEnumerable<BehaviorTreeNode> GetChildren() => Array.Empty<BehaviorTreeNode>();

        // ==========================================
        // 核心执行逻辑：永远受 CancellationToken 控制的异步 Tick 外壳！
        // ==========================================
        public async Task<NodeStatus> ExecuteTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            // 防御性急停
            ct.ThrowIfCancellationRequested();

            // 【核心修复】：在进入耗时任务之前，必须先切入 Running 状态！
            // 这样 Dashboard 才能瞬间亮起黄灯！
            if (Status != NodeStatus.Running)
            {
                Status = NodeStatus.Running;
            }

            // 核心执行 (调用子类的真实逻辑，这里可能会阻塞几秒钟)
            NodeStatus finalStatus = await OnTickAsync(blackboard, ct);

            // 执行完毕，更新为最终状态 (Success 或 Failure)
            Status = finalStatus;

            return Status;
        }

        // 留给子类去具体重写的业务逻辑
        protected abstract Task<NodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct);

        // 重置节点状态 (用于循环或重试)
        public virtual void Halt()
        {
            Status = NodeStatus.Idle;
            foreach (var child in GetChildren())
            {
                child.Halt();
            }
        }
    }
}