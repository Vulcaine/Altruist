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
    Custom,
}

public static class QuestHooks
{
    public const string Enter = "enter";
    public const string Leave = "leave";
    public const string Level = "level";
    public const string Kill = "kill";
    public const string Npc = "npc";
    public const string Item = "item";
    public const string Button = "button";
    public const string Timer = "timer";
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
    public string HookKey { get; set; } = "";
    public long TargetId { get; init; }
    public int Value { get; init; }
    public IQuestState State { get; init; } = null!;
    public IReadOnlyList<QuestRequirementResult> RequirementResults { get; init; } = Array.Empty<QuestRequirementResult>();
    public IServiceProvider? Services { get; init; }
}

public abstract class QuestBehavior<TContext> where TContext : QuestContext
{
}

public interface IQuestLevelResetHook<TContext> where TContext : QuestContext
{
    void ResetAboveLevel(TContext ctx, int level);
}
