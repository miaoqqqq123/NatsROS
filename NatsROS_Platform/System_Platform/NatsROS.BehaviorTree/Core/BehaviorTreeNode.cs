using System.ComponentModel;
using NatsROS.Core.SystemMessages;

namespace NatsROS.BehaviorTree.Core
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
        public BtNodeType NodeType { get; protected set; }

        //每个节点专属的汇报器
        public Action<string, BtNodeStatus>? Reporter { get; private set; }

        private BtNodeStatus _status = BtNodeStatus.Idle;
        public BtNodeStatus Status
        {
            get => _status;
            protected set
            {
                if (_status != value)
                {
                    _status = value;
                    // 【修复】：使用节点自己的专属汇报器！
                    Reporter?.Invoke(this.Id, _status);
                }
            }
        }

        protected BehaviorTreeNode(string name, BtNodeType type)
        {
            Name = name;
            NodeType = type;
        }


        /// <summary>
        /// 【核心黑魔法】：递归向下级节点传染汇报器！
        /// </summary>
        /// <param name="reporter"></param>
        public virtual void SetReporter(Action<string, BtNodeStatus> reporter)
        {
            Reporter = reporter;
            foreach (var child in GetChildren())
            {
                child.SetReporter(reporter); // 让所有的子节点也用同一个汇报器
            }
        }

        /// <summary>
        /// 允许外部遍历树结构 (用于画拓扑图)
        /// </summary>
        /// <returns></returns>
        public virtual IEnumerable<BehaviorTreeNode> GetChildren() => Array.Empty<BehaviorTreeNode>();


        /// <summary>
        /// 核心执行逻辑：永远受 CancellationToken 控制的异步 Tick 外壳！
        /// </summary>
        /// <param name="blackboard"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<BtNodeStatus> ExecuteTickAsync(Blackboard blackboard, CancellationToken ct)
        {
            // 防御性急停
            ct.ThrowIfCancellationRequested();

            // 【核心修复】：在进入耗时任务之前，必须先切入 Running 状态！
            // 这样 Dashboard 才能瞬间亮起黄灯！
            if (Status != BtNodeStatus.Running)
            {
                Status = BtNodeStatus.Running;
            }

            // 核心执行 (调用子类的真实逻辑，这里可能会阻塞几秒钟)
            BtNodeStatus finalStatus = await OnTickAsync(blackboard, ct);

            // 执行完毕，更新为最终状态 (Success 或 Failure)
            Status = finalStatus;

            return Status;
        }

        /// <summary>
        /// 留给子类去具体重写的业务逻辑
        /// </summary>
        /// <param name="blackboard"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        protected abstract Task<BtNodeStatus> OnTickAsync(Blackboard blackboard, CancellationToken ct);

        /// <summary>
        /// 重置节点状态 (用于循环或重试)
        /// </summary>
        public virtual void Halt()
        {
            Status = BtNodeStatus.Idle;
            foreach (var child in GetChildren())
            {
                child.Halt();
            }
        }
    }
}