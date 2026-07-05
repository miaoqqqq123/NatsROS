using MessagePack;

namespace NatsROS.Core.Communication;

public enum ActionStatus : byte
{
    Succeeded = 1,
    Aborted = 2,
    Canceled = 3
}

[MessagePackObject]
public record ActionCancelReq(
    [property: Key(0)] string GoalId
 ) : IRosMessage;

[MessagePackObject]
public record ActionGoal<TGoal, TResult>(
    [property: Key(0)] string GoalId,
    [property: Key(1)] TGoal Data
) : IRosRequest<ActionResult<TResult>>
    where TResult : IRosMessage;

[MessagePackObject]
public record ActionFeedback<TFeedback>(
    [property: Key(0)] string GoalId,
    [property: Key(1)] TFeedback Data
) : IRosMessage;

[MessagePackObject]
public record ActionResult<TResult>(
    [property: Key(0)] string GoalId,
    [property: Key(1)] ActionStatus Status,
    [property: Key(2)] TResult Data
) : IRosMessage;
