using NATS.Client.Core;
using NATS.Net;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NatsROS.Core.Communication;


/// <summary>
/// Action 服务端 (执行长任务)
/// </summary>
/// <typeparam name="TGoal"></typeparam>
/// <typeparam name="TFeedback"></typeparam>
/// <typeparam name="TResult"></typeparam>
/// <param name="nats"></param>
/// <param name="actionName"></param>

public class RosActionServer<TGoal, TFeedback, TResult>(INatsClient nats, string actionName)
    where TGoal : IRosActionGoal<TFeedback, TResult>
    where TFeedback : IRosMessage 
    where TResult : IRosMessage 
{
    private readonly RosServiceServer<ActionGoal<TGoal, TResult>, ActionResult<TResult>> _goalServer = new(nats, $"{actionName}.goal");
    private readonly RosSubscriber<ActionCancelReq> _cancelSub = new(nats, $"{actionName}.cancel", RosQosProfile.SensorData);
    private readonly RosPublisher<ActionFeedback<TFeedback>> _feedbackPub = new(nats, $"{actionName}.feedback", RosQosProfile.SensorData);

    // 保存所有正在执行的任务的 Token，用于接收分布式取消指令
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeGoals = new();

    /// <summary>
    /// 监听并执行动作。
    /// handler 参数说明：入参(目标数据, 发送反馈的委托, 取消Token)，返回(最终结果)
    /// </summary>
    public async Task ServeAsync(Func<TGoal, Action<TFeedback>, CancellationToken, Task<TResult>> handler, CancellationToken ct = default)
    {
        // 1. 后台监听取消指令
        _ = Task.Run(async () => {
            await foreach (var msg in _cancelSub.SubscribeAsync(ct))
            {
                if (msg == null) continue;

                // 【核心魔法】：如果收到 "*" 或空字符串，瞬间中断该 Action 下所有的活动任务！
                if (string.IsNullOrEmpty(msg.GoalId) || msg.GoalId == "*")
                {
                    foreach (var cts in _activeGoals.Values)
                    {
                        cts.Cancel();
                    }
                }
                else if (_activeGoals.TryGetValue(msg.GoalId, out var cts))
                {
                    // 否则，只精确取消对应的那个 GoalId
                    cts.Cancel();
                }
            }
        }, ct);

        // 2. 监听并处理目标请求
        await _goalServer.ServeAsync(async req =>
        {
            var goalId = req.GoalId;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeGoals[goalId] = cts;

            try
            {
                // 执行用户的业务逻辑
                var finalResult = await handler(req.Data, feedback => {
                    // 用户调用反馈委托时，推送到网络
                    _ = _feedbackPub.PublishAsync(new ActionFeedback<TFeedback>(goalId, feedback));
                }, cts.Token);

                return new ActionResult<TResult>(goalId, ActionStatus.Succeeded, finalResult);
            }
            catch (OperationCanceledException)
            {
                return new ActionResult<TResult>(goalId, ActionStatus.Canceled, default!);
            }
            catch (Exception)
            {
                return new ActionResult<TResult>(goalId, ActionStatus.Aborted, default!);
            }
            finally
            {
                _activeGoals.TryRemove(goalId, out _);
            }
        }, ct);
    }
}


/// <summary>
/// Action 客户端 (发起任务与获取反馈)
/// </summary>
/// <typeparam name="TGoal"></typeparam>
/// <typeparam name="TFeedback"></typeparam>
/// <typeparam name="TResult"></typeparam>
/// <param name="nats"></param>
/// <param name="actionName"></param>
public class RosActionClient<TGoal, TFeedback, TResult>(INatsClient nats, string actionName)
    where TGoal : IRosActionGoal<TFeedback, TResult>
    where TFeedback : IRosMessage 
    where TResult : IRosMessage
{
    private readonly RosServiceClient<ActionGoal<TGoal, TResult>, ActionResult<TResult>> _goalClient = new(nats, $"{actionName}.goal");
    private readonly RosPublisher<ActionCancelReq> _cancelPub = new(nats, $"{actionName}.cancel", RosQosProfile.SensorData);
    private readonly RosSubscriber<ActionFeedback<TFeedback>> _feedbackSub = new(nats, $"{actionName}.feedback", RosQosProfile.SensorData);

    public async Task<ActionResult<TResult>?> SendGoalAsync(TGoal goal, Action<TFeedback>? onFeedback = null, CancellationToken ct = default)
    {
        var goalId = Guid.NewGuid().ToString();

        // 1. 开启后台监听进度反馈
        using var feedbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var feedbackTask = Task.Run(async () => {
            await foreach (var msg in _feedbackSub.SubscribeAsync(feedbackCts.Token))
            {
                // 只处理当前 GoalId 的反馈
                if (msg != null && msg.GoalId == goalId)
                {
                    onFeedback?.Invoke(msg.Data);
                }
            }
        });

        try
        {
            // 2. 发起 RPC 等待最终结果 (动作可能很长，这里设置无限超时，依赖 ct 控制)
            return await _goalClient.CallAsync(new ActionGoal<TGoal, TResult>(goalId, goal), Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            // 任务结束，停止监听反馈
            feedbackCts.Cancel();
        }
    }

    // 暴露分布式取消的方法
    public Task CancelGoalAsync(string goalId) => _cancelPub.PublishAsync(new ActionCancelReq(goalId)).AsTask();
}
