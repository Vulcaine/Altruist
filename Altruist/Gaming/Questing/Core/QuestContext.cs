namespace Altruist.Gaming.Questing;

public enum QuestTrigger
{
    Enter,
    Leave,
    Level,
    Kill,
    Npc,
    Item,
    Button,
    Timer,
    Scroll,
}

public enum QuestStatus
{
    Eligible,
    Suspended,
    Active,
    Completed,
    Failed,
}

public sealed record QuestRequirement(string Key, QuestOperator Operator, object Value);

public sealed record QuestRequirementResult(string Key, bool Passed, string? Reason = null);

public sealed record QuestUpdate(
    string SubjectId,
    string QuestId,
    string Name,
    string Category,
    QuestStatus Status,
    string State,
    string CounterText,
    IReadOnlyList<QuestRequirementResult> Requirements);

public class QuestContext
{
    public string SubjectId { get; init; } = "";
    public object? Subject { get; init; }
    public QuestTrigger Trigger { get; init; }
    public long TargetId { get; init; }
    public int Value { get; init; }
    public IQuestState State { get; init; } = null!;
    public IReadOnlyList<QuestRequirementResult> RequirementResults { get; init; } = Array.Empty<QuestRequirementResult>();
    public IServiceProvider? Services { get; init; }
}

public abstract class QuestBehavior<TContext> where TContext : QuestContext
{
    public virtual Task OnEnter(TContext ctx) => Task.CompletedTask;
    public virtual Task OnLeave(TContext ctx) => Task.CompletedTask;
    public virtual Task OnLevel(TContext ctx) => Task.CompletedTask;
    public virtual Task OnKill(TContext ctx) => Task.CompletedTask;
    public virtual Task OnNpc(TContext ctx) => Task.CompletedTask;
    public virtual Task OnItem(TContext ctx) => Task.CompletedTask;
    public virtual Task OnButton(TContext ctx) => Task.CompletedTask;
    public virtual Task OnTimer(TContext ctx) => Task.CompletedTask;
    public virtual Task OnScroll(TContext ctx) => Task.CompletedTask;
}

public interface IQuestLevelResetHook<TContext> where TContext : QuestContext
{
    void ResetAboveLevel(TContext ctx, int level);
}
