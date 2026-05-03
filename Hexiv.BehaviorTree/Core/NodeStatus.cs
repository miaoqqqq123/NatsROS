using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hexiv.BehaviorTree.Core
{
    // 完全对标 BehaviorTree.CPP 的状态
    public enum NodeStatus : byte
    {
        Idle = 0,    // 未执行 / 已重置
        Running = 1, // 正在执行长任务 (异步)
        Success = 2, // 执行成功
        Failure = 3  // 执行失败
    }

    // 节点类型，专供 NatsROS Dashboard 画拓扑图使用
    public enum BTNodeType : byte
    {
        Root=0,
        Sequence = 1,
        Selector = 2,
        Action = 3,
        Decorator = 4, // 菱形 (例如 Retry)
        Condition=5
    }
}
