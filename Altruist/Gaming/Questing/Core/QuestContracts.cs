namespace Altruist.Gaming.Questing;

/// <summary>
/// Persistence for per-subject quest state, supplied by the game (e.g. a database repository)
/// to <see cref="QuestRuntime{T}"/>. Keys of the dictionaries are quest ids.
/// </summary>
/// <remarks>
/// The runtime loads a subject once and caches the returned dictionary in memory (it keeps
/// mutating the same <see cref="QuestState"/> instances), then calls
/// <see cref="SaveDirtyAsync"/> after every fire. Persist <see cref="QuestState.Serialize"/>
/// for entries whose <see cref="QuestState.IsDirty"/> is true and call
/// <see cref="QuestState.MarkClean"/> afterwards; restore with the
/// <see cref="QuestState(string)"/> constructor.
/// </remarks>
public interface IQuestStateStore
{
    /// <summary>Loads every stored quest state of a subject (empty dictionary when none).</summary>
    /// <param name="subjectId">The subject id.</param>
    /// <returns>Quest id to state map; the runtime takes ownership of it.</returns>
    Task<Dictionary<string, QuestState>> LoadAsync(string subjectId);
    /// <summary>Persists changed states of a subject. Called after every runtime fire, even if nothing changed.</summary>
    /// <param name="subjectId">The subject id.</param>
    /// <param name="states">All cached states of the subject; filter by <see cref="QuestState.IsDirty"/>.</param>
    Task SaveDirtyAsync(string subjectId, IReadOnlyDictionary<string, QuestState> states);
    /// <summary>Deletes all stored quest state of a subject (called by <see cref="QuestRuntime{T}.ResetAsync"/>).</summary>
    /// <param name="subjectId">The subject id.</param>
    Task ResetAsync(string subjectId);
}

/// <summary>
/// Receives <see cref="QuestUpdate"/> snapshots whenever a quest's visible status changes for a
/// subject (typically forwards them to the client). Use <see cref="NullQuestUpdateSink{T}"/>
/// when no updates are needed.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public interface IQuestUpdateSink<TContext> where TContext : QuestContext
{
    /// <summary>Publishes one update. Called inline on the dispatching thread.</summary>
    /// <param name="context">The context of the dispatch that produced the update.</param>
    /// <param name="update">The new quest snapshot.</param>
    Task PublishAsync(TContext context, QuestUpdate update);
}

/// <summary>No-op <see cref="IQuestUpdateSink{T}"/> for servers or tests that do not publish quest updates.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class NullQuestUpdateSink<TContext> : IQuestUpdateSink<TContext> where TContext : QuestContext
{
    /// <inheritdoc/>
    public Task PublishAsync(TContext context, QuestUpdate update) => Task.CompletedTask;
}

/// <summary>Input of an <see cref="IQuestRequirementEvaluator{T}"/>: the dispatch context and the requirement to check.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class QuestRequirementContext<TContext> where TContext : QuestContext
{
    /// <summary>Context of the pending dispatch (its <see cref="QuestContext.RequirementResults"/> is empty).</summary>
    public required TContext QuestContext { get; init; }
    /// <summary>The requirement being evaluated (key, operator, value).</summary>
    public required QuestRequirement Requirement { get; init; }
}

/// <summary>
/// Evaluates requirements with a given <see cref="Key"/> (e.g. <c>"level"</c>), comparing a
/// game value against <see cref="QuestRequirement.Value"/> using
/// <see cref="QuestRequirement.Operator"/>. Register in a <see cref="QuestRequirementRegistry{T}"/>;
/// for simple lambdas use the registry's <c>Add(key, func)</c> overloads instead of
/// implementing this interface.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public interface IQuestRequirementEvaluator<TContext> where TContext : QuestContext
{
    /// <summary>Requirement key handled (case-insensitive).</summary>
    string Key { get; }
    /// <summary>Returns true when the requirement is satisfied. Runs before every hook dispatch of the quest, so keep it cheap.</summary>
    /// <param name="context">The dispatch context and requirement.</param>
    /// <returns>Whether the requirement passes.</returns>
    ValueTask<bool> EvaluateAsync(QuestRequirementContext<TContext> context);
}

/// <summary>
/// Declares the hook key of a hook interface. Put it on an interface that extends
/// <see cref="IQuestHook{T}"/> and has exactly one method returning <see cref="Task"/> with a
/// single context parameter; quests implementing it receive that hook via
/// <see cref="QuestRuntime{T}.FireHookAsync"/>. This is how custom triggers are added without
/// changing the framework.
/// </summary>
/// <example>
/// <code>
/// [QuestHook("craft")]
/// public interface IOnCraft : IQuestHook&lt;MyQuestContext&gt;
/// {
///     Task OnCraft(MyQuestContext ctx);
/// }
///
/// // fire it:
/// await runtime.FireHookAsync(baseCtx, "craft", targetId: recipeId);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class QuestHookAttribute : Attribute
{
    /// <summary>The hook key (matched case-insensitively).</summary>
    public string Key { get; }

    /// <summary>Declares the hook key.</summary>
    /// <param name="key">Hook key, e.g. <c>"craft"</c>.</param>
    public QuestHookAttribute(string key)
    {
        Key = key;
    }
}

/// <summary>
/// Marker base of hook interfaces. Only interfaces deriving from it and decorated with
/// <see cref="QuestHookAttribute"/> are compiled into quest hook handlers.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public interface IQuestHook<TContext> where TContext : QuestContext
{
}

/// <summary>Handles <see cref="QuestHooks.Enter"/> (subject entered; also fired by <see cref="QuestRuntime{T}.ReconcileAsync"/>). Use for initialisation and re-sync on login.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Enter)]
public interface IOnEnter<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the enter hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnEnter(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Leave"/> (subject left).</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Leave)]
public interface IOnLeave<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the leave hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnLeave(TContext ctx);
}

/// <summary>
/// Handles <see cref="QuestHooks.Level"/>. With <see cref="QuestRuntime{T}.ReconcileLevelAsync"/> it
/// is called once per level not yet reconciled, with <see cref="QuestContext.Value"/> = that
/// level; pair with <see cref="IQuestLevelResetHook{T}"/> to handle level loss.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Level)]
public interface IOnLevel<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the level hook.</summary>
    /// <param name="ctx">Dispatch context; <see cref="QuestContext.Value"/> is the level.</param>
    Task OnLevel(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Kill"/>; <see cref="QuestContext.TargetId"/> identifies the victim.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Kill)]
public interface IOnKill<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the kill hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnKill(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Npc"/>; combine with <see cref="QuestNpcAttribute"/> to receive only interactions with bound NPCs.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Npc)]
public interface IOnNpc<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the npc hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnNpc(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Item"/>; <see cref="QuestContext.TargetId"/> / <see cref="QuestContext.Value"/> carry game-defined item data.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Item)]
public interface IOnItem<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the item hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnItem(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Button"/> (quest UI button pressed).</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Button)]
public interface IOnButton<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the button hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnButton(TContext ctx);
}

/// <summary>Handles <see cref="QuestHooks.Timer"/>. The framework has no scheduler; the game fires <see cref="QuestTrigger.Timer"/> itself.</summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
[QuestHook(QuestHooks.Timer)]
public interface IOnTimer<TContext> : IQuestHook<TContext> where TContext : QuestContext
{
    /// <summary>Called on the timer hook.</summary>
    /// <param name="ctx">Dispatch context.</param>
    Task OnTimer(TContext ctx);
}

/// <summary>
/// <see cref="IQuestRequirementEvaluator{T}"/> backed by a delegate. Usually created through
/// the <see cref="QuestRequirementRegistry{T}"/> <c>Add(key, func)</c> overloads.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class DelegateQuestRequirementEvaluator<TContext> : IQuestRequirementEvaluator<TContext>
    where TContext : QuestContext
{
    private readonly Func<QuestRequirementContext<TContext>, ValueTask<bool>> _evaluate;
    /// <inheritdoc/>
    public string Key { get; }

    /// <summary>Creates an evaluator for <paramref name="key"/>.</summary>
    /// <param name="key">Requirement key handled.</param>
    /// <param name="evaluate">Evaluation function.</param>
    public DelegateQuestRequirementEvaluator(string key, Func<QuestRequirementContext<TContext>, ValueTask<bool>> evaluate)
    {
        Key = key;
        _evaluate = evaluate;
    }

    /// <inheritdoc/>
    public ValueTask<bool> EvaluateAsync(QuestRequirementContext<TContext> context) => _evaluate(context);
}

/// <summary>
/// Maps requirement keys (case-insensitive) to evaluators; passed to the
/// <see cref="QuestRuntime{T}"/> constructor. A requirement whose key is not registered always
/// fails with reason <c>"missing_evaluator"</c>. Adding a key twice replaces the earlier
/// evaluator. Not thread-safe: populate it before the runtime starts dispatching.
/// </summary>
/// <example>
/// <code>
/// var requirements = new QuestRequirementRegistry&lt;MyQuestContext&gt;()
///     .Add("level", r =&gt; Compare(r.QuestContext.Level, r.Requirement.Operator, Convert.ToInt32(r.Requirement.Value)));
/// </code>
/// </example>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class QuestRequirementRegistry<TContext> where TContext : QuestContext
{
    private readonly Dictionary<string, IQuestRequirementEvaluator<TContext>> _evaluators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) an evaluator under its <see cref="IQuestRequirementEvaluator{T}.Key"/>.</summary>
    /// <param name="evaluator">The evaluator.</param>
    /// <returns>This registry, for chaining.</returns>
    public QuestRequirementRegistry<TContext> Add(IQuestRequirementEvaluator<TContext> evaluator)
    {
        _evaluators[evaluator.Key] = evaluator;
        return this;
    }

    /// <summary>Registers (or replaces) a synchronous evaluator for <paramref name="key"/>.</summary>
    /// <param name="key">Requirement key.</param>
    /// <param name="evaluator">Returns true when the requirement passes.</param>
    /// <returns>This registry, for chaining.</returns>
    public QuestRequirementRegistry<TContext> Add(string key, Func<QuestRequirementContext<TContext>, bool> evaluator)
    {
        return Add(new DelegateQuestRequirementEvaluator<TContext>(key, ctx => ValueTask.FromResult(evaluator(ctx))));
    }

    /// <summary>Registers (or replaces) an asynchronous evaluator for <paramref name="key"/>.</summary>
    /// <param name="key">Requirement key.</param>
    /// <param name="evaluator">Returns true when the requirement passes.</param>
    /// <returns>This registry, for chaining.</returns>
    public QuestRequirementRegistry<TContext> Add(string key, Func<QuestRequirementContext<TContext>, ValueTask<bool>> evaluator)
    {
        return Add(new DelegateQuestRequirementEvaluator<TContext>(key, evaluator));
    }

    /// <summary>Looks up the evaluator for <paramref name="key"/> (case-insensitive).</summary>
    /// <param name="key">Requirement key.</param>
    /// <param name="evaluator">The evaluator when found.</param>
    /// <returns>True when an evaluator is registered.</returns>
    public bool TryGet(string key, out IQuestRequirementEvaluator<TContext> evaluator) =>
        _evaluators.TryGetValue(key, out evaluator!);
}
