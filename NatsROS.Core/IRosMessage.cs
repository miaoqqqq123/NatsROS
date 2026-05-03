using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatsROS.Core;

/// <summary>
/// ROS 消息的基类标记接口。所有需要在 NatsROS 中传输的数据模型都应实现此接口。
/// </summary>
public interface IRosMessage { }

/// <summary>
/// 同步服务请求接口。强制绑定其对应的响应类型！
/// </summary>
/// <typeparam name="TResponse"></typeparam>
public interface IRosRequest<TResponse> : IRosMessage
    where TResponse : IRosMessage
{ }

/// <summary>
/// 【核心魔法 2】：Action 动作目标接口。强制绑定其对应的反馈和结果类型！
/// </summary>
/// <typeparam name="TFeedback"></typeparam>
/// <typeparam name="TResult"></typeparam>
public interface IRosActionGoal<TFeedback, TResult> : IRosMessage
    where TFeedback : IRosMessage
    where TResult : IRosMessage
{ }

