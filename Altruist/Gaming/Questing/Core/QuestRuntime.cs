using System.Reflection;

namespace Altruist.Gaming.Questing;

public sealed class QuestRuntime<TContext> where TContext : QuestContext
{
    private const string LevelReconciledKey = "__level_reconciled";

    private readonly IQuestStateStore _stateStore;
    private readonly IQuestUpdateSink<TContext> _updateSink;
    private readonly QuestRequirementRegistry<TContext> _requirements;
    private readonly Func<TContext, QuestTrigger, QuestDefinition<TContext>, long, int, QuestState, IReadOnlyList<QuestRequirementResult>, TContext> _contextFactory;
    private readonly Dictionary<string, Dictionary<string, QuestState>> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastUpdateFingerprints = new(StringComparer.Ordinal);
    private List<QuestDefinition<TContext>> _quests = new();
    private Dictionary<string, List<QuestDefinition<TContext>>> _npcBindings = new(StringComparer.OrdinalIgnoreCase);

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

    public int QuestCount => _quests.Count;
    public IReadOnlyList<QuestDefinition<TContext>> Quests => _quests;

    public void LoadFromAssembly(Assembly assembly)
    {
        var quests = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.ContainsGenericParameters && IsQuestType(t))
            .Select(CreateDefinition)
            .OrderBy(q => q.Id, StringComparer.Ordinal)
            .ToList();

        _quests = quests;
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

    public bool HasNpcBinding(string npcKey) =>
        !string.IsNullOrWhiteSpace(npcKey) && _npcBindings.ContainsKey(npcKey);

    public async Task FireAsync(TContext baseContext, QuestTrigger trigger, long targetId = 0, int value = 0, string? npcKey = null)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);
        string hookKey = QuestDefinition<TContext>.QuestTriggerToHookKey(trigger);
        IEnumerable<QuestDefinition<TContext>> targets = trigger == QuestTrigger.Npc && !string.IsNullOrWhiteSpace(npcKey)
            && _npcBindings.TryGetValue(npcKey, out var bound)
                ? bound
                : _quests;

        foreach (var quest in targets)
            await DispatchOneAsync(baseContext, quest, trigger, hookKey, targetId, value);

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

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

    public async Task FireHookAsync(TContext baseContext, string hookKey, long targetId = 0, int value = 0)
    {
        await EnsureStatesLoadedAsync(baseContext.SubjectId);

        foreach (var quest in _quests.Where(q => q.HasHook(hookKey)))
            await DispatchOneAsync(baseContext, quest, QuestTrigger.Custom, hookKey, targetId, value);

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

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

    public Task ReconcileAsync(TContext baseContext)
    {
        return FireAsync(baseContext, QuestTrigger.Enter);
    }

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
                await quest.DispatchHookAsync(ctx, QuestHooks.Level);
                state.Set(LevelReconciledKey, level);
            }

            await PublishAsync(ctx, quest, ResolveStatus(state), evaluation);
        }

        await _stateStore.SaveDirtyAsync(baseContext.SubjectId, _states[baseContext.SubjectId]);
    }

    public async Task ResetAsync(string subjectId)
    {
        _states.Remove(subjectId);
        foreach (var key in _lastUpdateFingerprints.Keys.Where(k => k.StartsWith(subjectId + ":", StringComparison.Ordinal)).ToArray())
            _lastUpdateFingerprints.Remove(key);
        await _stateStore.ResetAsync(subjectId);
    }

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
        if (!_states.TryGetValue(subjectId, out var map))
        {
            map = new Dictionary<string, QuestState>(StringComparer.Ordinal);
            _states[subjectId] = map;
        }

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

        _states[subjectId] = await _stateStore.LoadAsync(subjectId);
    }

    private static bool AllPassed(IReadOnlyList<QuestRequirementResult> results) =>
        results.Count == 0 || results.All(r => r.Passed);

    private static bool IsQuestType(Type type)
    {
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
