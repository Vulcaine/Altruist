namespace Altruist.Gaming.Questing;

/// <summary>
/// Game event kinds that the <see cref="QuestRuntime{T}"/> routes to quests. Each built-in
/// trigger maps to a <see cref="QuestHooks"/> key (see
/// <see cref="QuestDefinition{T}.QuestTriggerToHookKey"/>) and to a hook interface such as
/// <see cref="IOnKill{T}"/>.
/// </summary>
public enum QuestTrigger
{
    /// <summary>Subject entered the game/area; also used by <see cref="QuestRuntime{T}.ReconcileAsync"/>. Hook <see cref="QuestHooks.Enter"/>.</summary>
    Enter,
    /// <summary>Subject left the game/area. Hook <see cref="QuestHooks.Leave"/>.</summary>
    Leave,
    /// <summary>Subject level changed; prefer <see cref="QuestRuntime{T}.ReconcileLevelAsync"/>, which replays every level. Hook <see cref="QuestHooks.Level"/>.</summary>
    Level,
    /// <summary>Subject killed something (<see cref="QuestContext.TargetId"/> = victim id). Hook <see cref="QuestHooks.Kill"/>.</summary>
    Kill,
    /// <summary>Subject interacted with an NPC; routed to <see cref="QuestNpcAttribute"/>-bound quests when an npc key is given. Hook <see cref="QuestHooks.Npc"/>.</summary>
    Npc,
    /// <summary>Subject used/obtained an item. Hook <see cref="QuestHooks.Item"/>.</summary>
    Item,
    /// <summary>Subject pressed a quest UI button. Hook <see cref="QuestHooks.Button"/>.</summary>
    Button,
    /// <summary>A quest timer elapsed. Hook <see cref="QuestHooks.Timer"/>.</summary>
    Timer,
    /// <summary>Custom hook dispatched via <see cref="QuestRuntime{T}.FireHookAsync"/> or <see cref="QuestRuntime{T}.FireQuestHookAsync"/>; maps to an empty hook key when passed to <see cref="QuestRuntime{T}.FireAsync"/>.</summary>
    Custom,
}

/// <summary>
/// Built-in hook keys (lower-case strings) used to route triggers to quest handlers. Custom
/// hooks use any other key declared with <see cref="QuestHookAttribute"/> on a hook interface
/// or derived from an <c>OnXxx</c> state-method name.
/// </summary>
public static class QuestHooks
{
    /// <summary>Hook key for <see cref="QuestTrigger.Enter"/> / <see cref="IOnEnter{T}"/>.</summary>
    public const string Enter = "enter";
    /// <summary>Hook key for <see cref="QuestTrigger.Leave"/> / <see cref="IOnLeave{T}"/>.</summary>
    public const string Leave = "leave";
    /// <summary>Hook key for <see cref="QuestTrigger.Level"/> / <see cref="IOnLevel{T}"/>.</summary>
    public const string Level = "level";
    /// <summary>Hook key for <see cref="QuestTrigger.Kill"/> / <see cref="IOnKill{T}"/>.</summary>
    public const string Kill = "kill";
    /// <summary>Hook key for <see cref="QuestTrigger.Npc"/> / <see cref="IOnNpc{T}"/>.</summary>
    public const string Npc = "npc";
    /// <summary>Hook key for <see cref="QuestTrigger.Item"/> / <see cref="IOnItem{T}"/>.</summary>
    public const string Item = "item";
    /// <summary>Hook key for <see cref="QuestTrigger.Button"/> / <see cref="IOnButton{T}"/>.</summary>
    public const string Button = "button";
    /// <summary>Hook key for <see cref="QuestTrigger.Timer"/> / <see cref="IOnTimer{T}"/>.</summary>
    public const string Timer = "timer";
}

/// <summary>Status of a quest for one subject, as reported in <see cref="QuestUpdate.Status"/>.</summary>
public enum QuestStatus
{
    /// <summary>Available but not started. Not produced by <see cref="QuestRuntime{T}"/> itself; reserved for game code.</summary>
    Eligible,
    /// <summary>At least one <see cref="QuestRequirementAttribute"/> requirement failed, so hooks are not dispatched.</summary>
    Suspended,
    /// <summary>Requirements pass and the quest is neither completed nor failed.</summary>
    Active,
    /// <summary>State key <c>done</c> or <c>_done</c> is true, or the current state is <c>complete</c>/<c>completed</c>/<c>done</c> (case-insensitive).</summary>
    Completed,
    /// <summary>State key <c>failed</c> is true (checked before completion).</summary>
    Failed,
}

/// <summary>
/// One requirement of a quest definition (from <see cref="QuestRequirementAttribute"/> or
/// built manually for <see cref="QuestDefinition{T}.Create"/>), interpreted by the
/// <see cref="IQuestRequirementEvaluator{T}"/> registered under <c>Key</c>.
/// </summary>
/// <param name="Key">Evaluator key (case-insensitive lookup).</param>
/// <param name="Operator">Comparison the evaluator should apply.</param>
/// <param name="Value">Boxed comparison value.</param>
public sealed record QuestRequirement(string Key, QuestOperator Operator, object Value);

/// <summary>Outcome of evaluating one <see cref="QuestRequirement"/> for a dispatch.</summary>
/// <param name="Key">The requirement key.</param>
/// <param name="Passed">Whether the evaluator returned true.</param>
/// <param name="Reason">Optional failure reason; the runtime sets <c>"missing_evaluator"</c> when no evaluator is registered for the key.</param>
public sealed record QuestRequirementResult(string Key, bool Passed, string? Reason = null);

/// <summary>
/// Snapshot of one quest for one subject, pushed to the <see cref="IQuestUpdateSink{T}"/>
/// after a dispatch (deduplicated: only sent when status, state, counter text or requirement
/// results changed) and returned by <see cref="QuestRuntime{T}.BuildUpdatesAsync"/>.
/// </summary>
/// <param name="SubjectId">Subject (usually player) id.</param>
/// <param name="QuestId">Stable quest id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Category">Category, normally a <see cref="QuestKind"/> name.</param>
/// <param name="Status">Resolved status.</param>
/// <param name="State">Current state-machine state name (empty for hook-only quests).</param>
/// <param name="CounterText">
/// Progress text: state key <c>_counter</c> if set, otherwise <c>"{counter}/{target}"</c> when a
/// <c>target</c> (or <c>target_count</c>) &gt; 0 exists, where counter is the first positive of
/// <c>counter</c>, <c>kills</c>, <c>count</c>; else empty.
/// </param>
/// <param name="Requirements">Requirement results of the evaluation that produced this update.</param>
public sealed record QuestUpdate(
    string SubjectId,
    string QuestId,
    string Name,
    string Category,
    QuestStatus Status,
    string State,
    string CounterText,
    IReadOnlyList<QuestRequirementResult> Requirements);

/// <summary>
/// Per-dispatch context passed to quest handlers and requirement evaluators. Games subclass it
/// to add their own services or subject data and use the subclass as the <c>TContext</c> type
/// argument everywhere (<see cref="QuestRuntime{T}"/>, <see cref="QuestBehavior{T}"/>, hook
/// interfaces).
/// </summary>
/// <remarks>
/// The caller passes a "base" context (subject info, services) to the runtime; for every quest
/// the runtime builds a fresh context through the factory given to
/// <see cref="QuestRuntime{T}"/>'s constructor, which must copy the base fields and fill in
/// <see cref="Trigger"/>, <see cref="TargetId"/>, <see cref="Value"/>, <see cref="State"/> and
/// <see cref="RequirementResults"/> from its arguments. <see cref="HookKey"/> is set by the
/// runtime after the factory returns.
/// </remarks>
public class QuestContext
{
    /// <summary>Id of the subject (usually a player/character) whose quest state is used; keys state storage.</summary>
    public string SubjectId { get; init; } = "";
    /// <summary>Optional game object for the subject (e.g. the player entity); opaque to the framework.</summary>
    public object? Subject { get; init; }
    /// <summary>The trigger being dispatched.</summary>
    public QuestTrigger Trigger { get; init; }
    /// <summary>The hook key being dispatched (built-in <see cref="QuestHooks"/> or custom); set by the runtime.</summary>
    public string HookKey { get; set; } = "";
    /// <summary>Trigger-specific target id (e.g. killed entity, NPC, item id); 0 when unused.</summary>
    public long TargetId { get; init; }
    /// <summary>Trigger-specific integer (e.g. the level being reconciled for <see cref="QuestTrigger.Level"/>, an item count); 0 when unused.</summary>
    public int Value { get; init; }
    /// <summary>Mutable persisted state of this quest for <see cref="SubjectId"/>.</summary>
    public IQuestState State { get; init; } = null!;
    /// <summary>Requirement results for this dispatch (empty while requirements are themselves being evaluated).</summary>
    public IReadOnlyList<QuestRequirementResult> RequirementResults { get; init; } = Array.Empty<QuestRequirementResult>();
    /// <summary>Optional service provider for handlers that need game services.</summary>
    public IServiceProvider? Services { get; init; }
}

/// <summary>
/// Base class of every quest. Derive from it with your context type, decorate with
/// <see cref="QuestAttribute"/>, and add behavior by implementing hook interfaces
/// (<see cref="IOnEnter{T}"/>, <see cref="IOnKill{T}"/>, custom <see cref="IQuestHook{T}"/>
/// interfaces) and/or <see cref="QuestStateAttribute"/> methods. One instance is shared by all
/// subjects, so keep per-subject data in <see cref="QuestContext.State"/>, never in fields.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public abstract class QuestBehavior<TContext> where TContext : QuestContext
{
}

/// <summary>
/// Optional interface for level-driven quests (<see cref="IOnLevel{T}"/>): called by
/// <see cref="QuestRuntime{T}.ReconcileLevelAsync"/> when the subject's level went down, so
/// the quest can undo progress granted above the new level before replay.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public interface IQuestLevelResetHook<TContext> where TContext : QuestContext
{
    /// <summary>Revert any state granted for levels above <paramref name="level"/>.</summary>
    /// <param name="ctx">Context of the level reconcile (<see cref="QuestContext.Value"/> = new level).</param>
    /// <param name="level">The new (lower) level, at least 1.</param>
    void ResetAboveLevel(TContext ctx, int level);
}
