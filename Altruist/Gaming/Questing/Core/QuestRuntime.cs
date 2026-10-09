using System.Collections.Concurrent;
using System.Reflection;

namespace Altruist.Gaming.Questing;

/// <summary>
/// The quest engine: holds the registered <see cref="QuestDefinition{T}"/>s, routes game events
/// (triggers and custom hooks) to them per subject, gates them by requirements, caches and
/// persists per-subject <see cref="QuestState"/> through an <see cref="IQuestStateStore"/>, and
/// publishes changed <see cref="QuestUpdate"/>s to an <see cref="IQuestUpdateSink{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not registered in DI by the framework: construct one per context type (typically a
/// singleton), call <see cref="LoadFromAssembly"/> then <see cref="LoadModulesFromAssembly"/>
/// once at startup, then fire events from game code. Loading/registering is not thread-safe
/// and must finish before dispatching.
/// </para>
/// <para>
/// Threading: different subjects may be dispatched concurrently, but calls for the same subject
/// must be serialized (per-subject state is a plain dictionary). Quests are visited in ordinal
/// id order after loading. Each dispatch: evaluate requirements (failing → publish
/// <see cref="QuestStatus.Suspended"/>, skip handler), run the state-machine or hook handler,
/// resolve status from state, publish if changed, and finally call
/// <see cref="IQuestStateStore.SaveDirtyAsync"/> once per fire.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var runtime = new QuestRuntime&lt;MyQuestContext&gt;(
///     store, new NullQuestUpdateSink&lt;MyQuestContext&gt;(), requirements,
///     (b, trigger, quest, targetId, value, state, results) =&gt; new MyQuestContext
///     {
///         SubjectId = b.SubjectId, Subject = b.Subject, Services = b.Services, Player = b.Player,
///         Trigger = trigger, TargetId = targetId, Value = value, State = state, RequirementResults = results,
///     });
/// runtime.LoadFromAssembly(typeof(WolfHunt).Assembly);
/// runtime.LoadModulesFromAssembly(typeof(WolfHunt).Assembly);
///
/// await runtime.ReconcileAsync(baseCtx);                                   // on login
/// await runtime.FireAsync(baseCtx, QuestTrigger.Kill, targetId: victimId); // on kill
/// await runtime.FireAsync(baseCtx, QuestTrigger.Npc, npcKey: "postmaster");
/// </code>
/// </example>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class QuestRuntime<TContext> where TContext : QuestContext
{
    private const string LevelReconciledKey = "__level_reconciled";

    private readonly IQuestStateStore _stateStore;
    private readonly IQuestUpdateSink<TContext> _updateSink;
    private readonly QuestRequirementRegistry<TContext> _requirements;
    private readonly Func<TContext, QuestTrigger, QuestDefinition<TContext>, long, int, QuestState, IReadOnlyList<QuestRequirementResult>, TContext> _contextFactory;
    // ConcurrentDictionary because parallel player sessions all dispatch quests
    // through the same QuestRuntime singleton — the prior plain Dictionary corrupted
    // its internal buckets when two sessions called EnsureStatesLoadedAsync /
    // GetState simultaneously, surfacing as "non-concurrent collections must have
    // exclusive access" InvalidOperationException and silently dropping every
    // quest's Enter dispatch for the unlucky session (including StarterGearQuest,
    // hence "no starter inventory" symptom in parallel E2E tests).
    private readonly ConcurrentDictionary<string, Dictionary<string, QuestState>> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastUpdateFingerprints = new(StringComparer.Ordinal);
    private List<QuestDefinition<TContext>> _quests = new();
    private Dictionary<string, List<QuestDefinition<TContext>>> _npcBindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty runtime; load quests with <see cref="LoadFromAssembly"/> / <see cref="Register"/>.</summary>
    /// <param name="stateStore">Per-subject state persistence.</param>
    /// <param name="updateSink">Receives changed quest snapshots (use <see cref="NullQuestUpdateSink{T}"/> to ignore).</param>
    /// <param name="requirements">Requirement evaluators by key.</param>
    /// <param name="contextFactory">
    /// Builds the per-quest context from (base context, trigger, quest, targetId, value, state,
    /// requirement results). Must copy the base context's subject data and set the other fields
    /// from the arguments; called for requirement evaluation (empty results, only when the quest has requirements) and again for
    /// the dispatch.
    /// </param>
    public QuestRuntime(
        IQuestStateStore stateStore,
        IQuestUpdateSink<TContext> updateSink,
        QuestRequirementRegistry<TContext> requirements,
        Func<TContext, QuestTrigger, QuestDefinition<TContext>, long, int, QuestState, IReadOnlyList<QuestRequirementResult>, TContext> contextFactory)
    {
        _stateStore = stateStore;
        _updateSink = updateSink;
        _requirements = requirements;
        _contextFactory = contextFactory;
    }

    /// <summary>Number of registered quests.</summary>
    public int QuestCount => _quests.Count;
    /// <summary>Registered quests (ordinal id order after loading; <see cref="Register"/> appends).</summary>
    public IReadOnlyList<QuestDefinition<TContext>> Quests => _quests;

    /// <summary>
    /// Discovers every concrete, non-generic <see cref="QuestBehavior{T}"/> subclass whose context
    /// type is assignable to <typeparamref name="TContext"/> (skipping
    /// <see cref="QuestTemplateAttribute"/> types), instantiates it with its parameterless
    /// constructor and builds a definition from its attributes. Replaces all previously registered
    /// quests, so call it before <see cref="LoadModulesFromAssembly"/> / <see cref="Register"/>.
    /// </summary>
    /// <param name="assembly">Assembly containing the quest classes.</param>
    /// <exception cref="MissingMethodException">A quest type has no public parameterless constructor.</exception>
    /// <exception cref="InvalidOperationException">A state method has an invalid signature (see <see cref="QuestStateAttribute"/>).</exception>
    public void LoadFromAssembly(Assembly assembly)
    {
        var quests = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && IsQuestType(t))
            .Select(CreateDefinition)
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .ToList();

        _quests = quests;
        RebuildNpcBindings();
    }

    /// <summary>
    /// Discovers <see cref="IQuestModule{T}"/> implementations in the assembly
    /// and invokes their <c>Register</c> method, allowing data-driven quest
    /// registration. Call this after <see cref="LoadFromAssembly"/>. Re-sorts all quests by id.
    /// </summary>
    /// <param name="assembly">Assembly containing the module classes (public parameterless constructors).</param>
    public void LoadModulesFromAssembly(Assembly assembly)
    {
        var moduleType = typeof(IQuestModule<TContext>);
        foreach (var type in assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && moduleType.IsAssignableFrom(t)))
        {
            var module = (IQuestModule<TContext>)Activator.CreateInstance(type)!;
            module.Register(this);
        }
        _quests = _quests.OrderBy(q => q.Id, StringComparer.Ordinal).ToList();
        RebuildNpcBindings();
    }

    /// <summary>
    /// Adds a programmatically-built quest definition. Used by
    /// <see cref="IQuestModule{T}"/> implementations to register N tiers / N
    /// data rows that share one behavior-template class. Appends without sorting or checking for
    /// duplicate ids; call during startup only.
    /// </summary>
    /// <param name="definition">The definition, usually from <see cref="QuestDefinition{T}.Create"/>.</param>
    public void Register(QuestDefinition<TContext> definition)
    {
        _quests.Add(definition);
        foreach (var npcKey in definition.NpcKeys)
        {
            if (!_npcBindings.TryGetValue(npcKey, out var list))
            {
                list = new List<QuestDefinition<TContext>>();
                _npcBindings[npcKey] = list;
            }
            list.Add(definition);
        }
    }

    private void RebuildNpcBindings()
    {
        _npcBindings = new Dictionary<string, List<QuestDefinition<TContext>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in _quests)
        {
            foreach (var npcKey in quest.NpcKeys)
            {
                if (!_npcBindings.TryGetValue(npcKey, out var list))
                {
                    list = new List<QuestDefinition<TContext>>();
                    _npcBindings[npcKey] = list;
                }
                list.Add(quest);
            }
        }
    }

    /// <summary>True when at least one quest is bound to <paramref name="npcKey"/> via <see cref="QuestNpcAttribute"/> (case-insensitive). Useful to decide whether an NPC interaction is quest-relevant.</summary>
    /// <param name="npcKey">NPC key.</param>
    /// <returns>Whether bindings exist.</returns>
    public bool HasNpcBinding(string npcKey) =>
        !string.IsNullOrWhiteSpace(npcKey) && _npcBindings.ContainsKey(npcKey);

    /// <summary>
    /// Fires a built-in trigger at every quest (each one's requirements are evaluated and its
    /// matching handler, if any, runs), then saves. This is the main entry point for game events.
    /// For <see cref="QuestTrigger.Npc"/> with an <paramref name="npcKey"/> that has bindings, only
    /// the bound quests are targeted; an unbound or missing key targets all quests. A handler
    /// exception is logged to stderr and does not stop other quests.
    /// </summary>
    /// <param name="baseContext">Subject data; passed to the context factory.</param>
    /// <param name="trigger">The event; use <see cref="FireHookAsync"/> for custom hooks.</param>
    /// <param name="targetId">Event target id (victim, NPC, item...), exposed as <see cref="QuestContext.TargetId"/>.</param>
    /// <param name="value">Event value, exposed as <see cref="QuestContext.Value"/>.</param>
    /// <param name="npcKey">NPC key for <see cref="QuestTrigger.Npc"/> routing; ignored for other triggers.</param>
    public async Task FireAsync(TContext baseContext, QuestTrigger trigger, long targetId = 0, int value = 0, string? npcKey = null)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);
        string hookKey = QuestDefinition<TContext>.QuestTriggerToHookKey(trigger);
        IEnumerable<QuestDefinition<TContext>> targets = trigger == QuestTrigger.Npc && !string.IsNullOrWhiteSpace(npcKey)
            && _npcBindings.TryGetValue(npcKey, out var bound)
                ? bound
                : _quests;

        foreach (var quest in targets)
        {
            // Isolate per-quest dispatch — a single faulting handler used to take down
            // every subsequent quest in the trigger chain (the outer try/catch in
            // QuestEngine logged the exception but the foreach had already stopped).
            // E.g. one OnKill handler that resolved an unregistered vnum would prevent
            // every other OnKill from firing for the same kill event.
            try
            {
                await DispatchOneAsync(baseContext, quest, trigger, hookKey, targetId, value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[QuestRuntime] {trigger} dispatch failed for quest '{quest.Id}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

    /// <summary>Fires a built-in trigger at one quest only (see <see cref="FireQuestHookAsync"/>).</summary>
    /// <param name="baseContext">Subject data.</param>
    /// <param name="questId">Target quest id (ordinal, case-sensitive).</param>
    /// <param name="trigger">The trigger.</param>
    /// <param name="targetId">Event target id.</param>
    /// <param name="value">Event value.</param>
    /// <returns>False when no quest has that id.</returns>
    public Task<bool> FireQuestAsync(TContext baseContext, string questId, QuestTrigger trigger, long targetId = 0, int value = 0)
    {
        return FireQuestHookAsync(
            baseContext,
            questId,
            QuestDefinition<TContext>.QuestTriggerToHookKey(trigger),
            trigger,
            targetId,
            value);
    }

    /// <summary>
    /// Fires a custom (or built-in) hook key at every quest whose <see cref="QuestDefinition{T}.HasHook"/>
    /// is true, then saves. Use for game-specific events declared with
    /// <see cref="QuestHookAttribute"/> or <c>OnXxx</c> state methods; the context's
    /// <see cref="QuestContext.Trigger"/> is <see cref="QuestTrigger.Custom"/>. Unlike
    /// <see cref="FireAsync"/>, a handler exception propagates and stops the remaining quests.
    /// </summary>
    /// <param name="baseContext">Subject data.</param>
    /// <param name="hookKey">Hook key (case-insensitive for handler lookup).</param>
    /// <param name="targetId">Event target id.</param>
    /// <param name="value">Event value.</param>
    public async Task FireHookAsync(TContext baseContext, string hookKey, long targetId = 0, int value = 0)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);

        foreach (var quest in _quests.Where(q => q.HasHook(hookKey)))
            await DispatchOneAsync(baseContext, quest, QuestTrigger.Custom, hookKey, targetId, value);

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

    /// <summary>
    /// Dispatches a hook key to a single quest by id (requirements, handler, publish), then saves.
    /// Use when the event is already known to concern one quest (e.g. a quest UI button). Exceptions
    /// from the handler propagate.
    /// </summary>
    /// <param name="baseContext">Subject data.</param>
    /// <param name="questId">Target quest id (ordinal, case-sensitive).</param>
    /// <param name="hookKey">Hook key.</param>
    /// <param name="trigger">Value exposed as <see cref="QuestContext.Trigger"/>.</param>
    /// <param name="targetId">Event target id.</param>
    /// <param name="value">Event value.</param>
    /// <returns>False when no quest has that id.</returns>
    public async Task<bool> FireQuestHookAsync(
        TContext baseContext,
        string questId,
        string hookKey,
        QuestTrigger trigger = QuestTrigger.Custom,
        long targetId = 0,
        int value = 0)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);

        var quest = _quests.FirstOrDefault(q => q.Id.Equals(questId, StringComparison.Ordinal));
        if (quest == null)
            return false;

        await DispatchOneAsync(baseContext, quest, trigger, hookKey, targetId, value);
        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
        return true;
    }

    /// <summary>Fires <see cref="QuestTrigger.Enter"/> at all quests; call when a subject logs in / enters so quests can initialise or re-sync.</summary>
    /// <param name="baseContext">Subject data.</param>
    public Task ReconcileAsync(TContext baseContext)
    {
        return FireAsync(baseContext, QuestTrigger.Enter);
    }

    /// <summary>
    /// Brings level-driven quests (<see cref="QuestDefinition{T}.HasLevelHook"/>) in line with the
    /// subject's level: for each such quest whose requirements pass, dispatches
    /// <see cref="QuestHooks.Level"/> once per level from the last reconciled level + 1 up to
    /// <paramref name="currentLevel"/> (<see cref="QuestContext.Value"/> = that level), remembering
    /// progress in state key <c>__level_reconciled</c>. Idempotent, so call it on login and on every
    /// level change instead of firing <see cref="QuestTrigger.Level"/> directly. On level loss
    /// (<paramref name="currentLevel"/> &lt; <paramref name="previousLevel"/>)
    /// <see cref="IQuestLevelResetHook{T}.ResetAboveLevel"/> runs first (even if requirements fail) and the reconciled level is
    /// lowered. Per-level handler exceptions are logged and skipped.
    /// </summary>
    /// <param name="baseContext">Subject data.</param>
    /// <param name="previousLevel">Level before the change (only used to detect level loss).</param>
    /// <param name="currentLevel">New level (clamped to at least 1).</param>
    public async Task ReconcileLevelAsync(TContext baseContext, int previousLevel, int currentLevel)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);
        int targetLevel = Math.Max(1, currentLevel);
        bool levelDown = currentLevel < previousLevel;

        foreach (var quest in _quests.Where(q => q.HasLevelHook))
        {
            var state = GetState(baseContext.SubjectId, quest.Id);
            var evaluation = await EvaluateRequirementsAsync(baseContext, quest, QuestTrigger.Level, QuestHooks.Level, 0, targetLevel, state);
            var ctx = _contextFactory(baseContext, QuestTrigger.Level, quest, 0, targetLevel, state, evaluation);
            ctx.HookKey = QuestHooks.Level;

            if (levelDown)
            {
                if (quest.Instance is IQuestLevelResetHook<TContext> reset)
                    reset.ResetAboveLevel(ctx, targetLevel);

                int reconciled = state.Get(LevelReconciledKey, 0);
                if (reconciled > targetLevel)
                    state.Set(LevelReconciledKey, targetLevel);
            }

            if (!AllPassed(evaluation))
            {
                await PublishAsync(ctx, quest, QuestStatus.Suspended, evaluation);
                continue;
            }

            int last = state.Get(LevelReconciledKey, 0);
            for (int level = last + 1; level <= targetLevel; level++)
            {
                ctx = _contextFactory(baseContext, QuestTrigger.Level, quest, 0, level, state, evaluation);
                ctx.HookKey = QuestHooks.Level;
                // Isolate per-quest level dispatch (see FireAsync for rationale). A
                // faulting OnLevel handler at level N must not stop reconciliation for
                // subsequent levels of the same quest, nor for other quests in the loop.
                try
                {
                    await quest.DispatchHookAsync(ctx, QuestHooks.Level);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[QuestRuntime] Level reconcile failed for quest '{quest.Id}' at level {level}: {ex.GetType().Name}: {ex.Message}");
                }
                state.Set(LevelReconciledKey, level);
            }

            await PublishAsync(ctx, quest, ResolveStatus(state), evaluation);
        }

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

    /// <summary>Drops the subject's cached state and publish fingerprints and calls <see cref="IQuestStateStore.ResetAsync"/>, restarting all of its quests.</summary>
    /// <param name="subjectId">The subject id.</param>
    public async Task ResetAsync(string subjectId)
    {
        _states.TryRemove(subjectId, out _);
        foreach (var key in _lastUpdateFingerprints.Keys.Where(k => k.StartsWith(subjectId + ":", StringComparison.Ordinal)).ToArray())
            _lastUpdateFingerprints.TryRemove(key, out _);
        await _stateStore.ResetAsync(subjectId);
    }

    /// <summary>
    /// Builds a full snapshot of every quest for the subject (e.g. to send the quest log on login)
    /// without running handlers or publishing. Requirements are evaluated with
    /// <see cref="QuestTrigger.Enter"/>; failing quests are reported <see cref="QuestStatus.Suspended"/>.
    /// </summary>
    /// <param name="baseContext">Subject data.</param>
    /// <returns>One update per registered quest, in quest order.</returns>
    public async Task<IReadOnlyList<QuestUpdate>> BuildUpdatesAsync(TContext baseContext)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);
        var updates = new List<QuestUpdate>();
        foreach (var quest in _quests)
        {
            var state = GetState(baseContext.SubjectId, quest.Id);
            var evaluation = await EvaluateRequirementsAsync(baseContext, quest, QuestTrigger.Enter, QuestHooks.Enter, 0, 0, state);
            var status = AllPassed(evaluation) ? ResolveStatus(state) : QuestStatus.Suspended;
            updates.Add(CreateUpdate(baseContext.SubjectId, quest, state, status, evaluation));
        }
        return updates;
    }

    /// <summary>Returns the live cached state of one quest for a subject (loading the subject if needed, creating an empty state if absent). Changes made to it are persisted on the next fire.</summary>
    /// <param name="subjectId">The subject id.</param>
    /// <param name="questId">The quest id.</param>
    /// <returns>The quest state.</returns>
    public async Task<QuestState> GetStateAsync(string subjectId, string questId)
    {
        await EnsureStatesLoadedAsync(subjectId);
        return GetState(subjectId, questId);
    }

    private async Task DispatchOneAsync(
        TContext baseContext,
        QuestDefinition<TContext> quest,
        QuestTrigger trigger,
        string hookKey,
        long targetId,
        int value)
    {
        var state = GetState(baseContext.SubjectId, quest.Id);
        var evaluation = await EvaluateRequirementsAsync(baseContext, quest, trigger, hookKey, targetId, value, state);
        var ctx = _contextFactory(baseContext, trigger, quest, targetId, value, state, evaluation);
        ctx.HookKey = hookKey;

        if (!AllPassed(evaluation))
        {
            await PublishAsync(ctx, quest, QuestStatus.Suspended, evaluation);
            return;
        }

        await quest.DispatchHookAsync(ctx, hookKey);
        await PublishAsync(ctx, quest, ResolveStatus(state), evaluation);
    }

    private async Task<IReadOnlyList<QuestRequirementResult>> EvaluateRequirementsAsync(
        TContext baseContext,
        QuestDefinition<TContext> quest,
        QuestTrigger trigger,
        string hookKey,
        long targetId,
        int value,
        QuestState state)
    {
        if (quest.Requirements.Count == 0)
            return Array.Empty<QuestRequirementResult>();

        var results = new List<QuestRequirementResult>(quest.Requirements.Count);
        var ctx = _contextFactory(baseContext, trigger, quest, targetId, value, state, Array.Empty<QuestRequirementResult>());
        ctx.HookKey = hookKey;

        foreach (var requirement in quest.Requirements)
        {
            if (!_requirements.TryGet(requirement.Key, out var evaluator))
            {
                results.Add(new QuestRequirementResult(requirement.Key, false, "missing_evaluator"));
                continue;
            }

            bool passed = await evaluator.EvaluateAsync(new QuestRequirementContext<TContext>
            {
                QuestContext = ctx,
                Requirement = requirement,
            });
            results.Add(new QuestRequirementResult(requirement.Key, passed));
        }

        return results;
    }

    private async Task PublishAsync(
        TContext ctx,
        QuestDefinition<TContext> quest,
        QuestStatus status,
        IReadOnlyList<QuestRequirementResult> requirements)
    {
        var update = CreateUpdate(ctx.SubjectId, quest, (QuestState)ctx.State, status, requirements);
        string key = $"{ctx.SubjectId}:{quest.Id}";
        string fingerprint = CreateUpdateFingerprint(update);
        if (_lastUpdateFingerprints.TryGetValue(key, out var last) && last == fingerprint)
            return;

        _lastUpdateFingerprints[key] = fingerprint;
        await _updateSink.PublishAsync(ctx, update);
    }

    private static QuestUpdate CreateUpdate(
        string subjectId,
        QuestDefinition<TContext> quest,
        QuestState state,
        QuestStatus status,
        IReadOnlyList<QuestRequirementResult> requirements)
    {
        return new QuestUpdate(
            subjectId,
            quest.Id,
            quest.Name,
            quest.Category,
            status,
            state.CurrentState,
            ResolveCounterText(state),
            requirements);
    }

    private static QuestStatus ResolveStatus(QuestState state)
    {
        if (state.Get("failed", false))
            return QuestStatus.Failed;
        if (state.Get("done", false) || state.Get("_done", false))
            return QuestStatus.Completed;

        var current = state.CurrentState ?? "";
        if (current.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || current.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || current.Equals("done", StringComparison.OrdinalIgnoreCase))
            return QuestStatus.Completed;

        return QuestStatus.Active;
    }

    private static string ResolveCounterText(QuestState state)
    {
        string explicitCounter = state.Get("_counter", string.Empty);
        if (!string.IsNullOrWhiteSpace(explicitCounter))
            return explicitCounter;

        int target = state.Get("target", 0);
        if (target <= 0)
            target = state.Get("target_count", 0);

        int counter = state.Get("counter", 0);
        if (counter <= 0)
        {
            counter = state.Get("kills", 0);
            if (counter <= 0)
                counter = state.Get("count", 0);
        }

        return target > 0 ? $"{counter}/{target}" : string.Empty;
    }

    private static string CreateUpdateFingerprint(QuestUpdate update)
    {
        var requirementText = string.Join(
            ',',
            update.Requirements.Select(r => $"{r.Key}:{r.Passed}:{r.Reason}"));
        return $"{update.Status}|{update.State}|{update.CounterText}|{requirementText}";
    }

    private QuestState GetState(string subjectId, string questId)
    {
        var map = _states.GetOrAdd(subjectId,
            _ => new Dictionary<string, QuestState>(StringComparer.Ordinal));

        // Inner map is per-subject; per-player session activity is serialized,
        // so a plain Dictionary is fine here. Multi-subject parallelism is the
        // case ConcurrentDictionary on the outer is solving.
        if (!map.TryGetValue(questId, out var state))
        {
            state = new QuestState();
            map[questId] = state;
        }

        return state;
    }

    private async Task EnsureStatesLoadedAsync(string subjectId)
    {
        if (_states.ContainsKey(subjectId))
            return;

        var loaded = await _stateStore.LoadAsync(subjectId);
        // TryAdd is race-safe: if another concurrent EnsureStatesLoadedAsync for
        // the same subject won the race, our just-loaded snapshot is discarded
        // (the winner's snapshot is equivalent — same store, same subjectId).
        _states.TryAdd(subjectId, loaded);
    }

    private static bool AllPassed(IReadOnlyList<QuestRequirementResult> results) =>
        results.Count == 0 || results.All(r => r.Passed);

    private static bool IsQuestType(Type type)
    {
        // Behavior templates are instantiated explicitly by IQuestModule<T> with
        // per-tier data — the assembly scan must skip them so they're not also
        // registered as a single zero-arg-constructor quest.
        if (type.GetCustomAttribute<QuestTemplateAttribute>(inherit: false) != null)
            return false;

        var current = type;
        while (current != null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(QuestBehavior<>))
                return current.GetGenericArguments()[0].IsAssignableTo(typeof(TContext));
            current = current.BaseType;
        }
        return false;
    }

    private static QuestDefinition<TContext> CreateDefinition(Type type)
    {
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Cannot instantiate quest {type.FullName}");

        var requirements = type.GetCustomAttributes<QuestRequirementAttribute>(false)
            .Select(a => new QuestRequirement(a.Key, a.Operator, a.Value))
            .ToArray();

        var npcKeys = type.GetCustomAttributes<QuestNpcAttribute>(false)
            .Select(a => a.NpcKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var kind = type.GetCustomAttribute<QuestKindAttribute>(false)?.Kind ?? QuestKind.Script;
        var name = type.GetCustomAttribute<QuestNameAttribute>(false)?.Name ?? type.Name;
        return new QuestDefinition<TContext>(
            QuestId.Resolve(type),
            name,
            kind.ToString(),
            npcKeys,
            requirements,
            (QuestBehavior<TContext>)instance,
            QuestStateDispatcher<TContext>.Compile(type, instance));
    }
}
