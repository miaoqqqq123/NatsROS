using NatsROS.BehaviorTree.Attributes;
using NatsROS.BehaviorTree.Core;
using NatsROS.Core.SystemMessages;

namespace HexivMachine.MissionControl
{
    [BtNode(DisplayName = "示例执行动作", Category = "示例工艺", Description = "在行为树中执行一次延时等待")]
    public class SampleAction : BtActionBase
    {
        [BtProp(DisplayName = "等待时间(ms)", DefaultValue = "1000")]
        public int WaitTime { get; set; } = 1000;

        public SampleAction(string name) : base(name) { }

        protected override async Task<BtNodeStatus> OnExecuteAsync(Blackboard blackboard, CancellationToken ct)
        {
            // 模拟业务耗时
            await Task.Delay(WaitTime, ct);
            return BtNodeStatus.Success;
        }
    }
}