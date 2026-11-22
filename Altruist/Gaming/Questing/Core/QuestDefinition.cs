using System.Reflection;

namespace Altruist.Gaming.Questing;

/// <summary>
/// A registered quest: id, metadata, requirements, the shared behavior instance and its
/// compiled hook / state handlers. Built automatically from attributes by
/// <see cref="QuestRuntime{T}.LoadFromAssembly"/>, or by hand with <see cref="Create"/> inside an
/// <see cref="IQuestModule{T}"/> when one behavior class backs several quests.
/// </summary>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public sealed class QuestDefinition<TContext> where TContext : QuestContext
{
    private readonly Dictionary<string, Func<TContext, Task>> _hookHandlers;

    /// <summary>Stable quest id; persistence key of the per-subject <see cref="QuestState"/>.</summary>
    public string Id { get; }
    /// <summary>Display name.</summary>
    public string Name { get; }
    /// <summary>Category string (normally a <see cref="QuestKind"/> name).</summary>
    public string Category { get; }
    /// <summary>NPC keys this quest is bound to for <see cref="QuestTrigger.Npc"/> routing (see <see cref="QuestNpcAttribute"/>).</summary>
    public IReadOnlyList<string> NpcKeys { get; }
    /// <summary>Requirements that must all pass before any hook is dispatched.</summary>
    public IReadOnlyList<QuestRequirement> Requirements { get; }
    /// <summary>The behavior instance, shared by every subject.</summary>
    public QuestBehavior<TContext> Instance { get; }
    /// <summary>Compiled <see cref="QuestStateAttribute"/> state machine (empty when the behavior declares no states).</summary>
    public QuestStateDispatcher<TContext> StateDispatcher { get; }
    /// <summary>True when the quest handles <see cref="QuestHooks.Level"/>; only such quests take part in <see cref="QuestRuntime{T}.ReconcileLevelAsync"/>.</summary>
    public bool HasLevelHook { get; }

    /// <summary>
    /// Creates a definition and compiles the hook-interface handlers of <paramref name="instance"/>.
    /// Prefer <see cref="Create"/>, which also compiles the state dispatcher.
    /// </summary>
    /// <param name="id">Stable quest id.</param>
    /// <param name="name">Display name.</param>
    /// <param name="category">Category string.</param>
    /// <param name="npcKeys">NPC keys for npc-trigger routing.</param>
    /// <param name="requirements">Requirements gating every dispatch.</param>
    /// <param name="instance">The behavior instance.</param>
    /// <param name="stateDispatcher">State machine compiled from the instance's type (<see cref="QuestStateDispatcher{T}.Compile"/>).</param>
    public QuestDefinition(
        string id,
        string name,
        string category,
        IReadOnlyList<string> npcKeys,
        IReadOnlyList<QuestRequirement> requirements,
        QuestBehavior<TContext> instance,
        QuestStateDispatcher<TContext> stateDispatcher)
    {
        Id = id;
        Name = name;
        Category = category;
        NpcKeys = npcKeys;
        Requirements = requirements;
        Instance = instance;
        StateDispatcher = stateDispatcher;
        _hookHandlers = CompileHookHandlers(instance);
        HasLevelHook = HasHook(QuestHooks.Level);
    }

    /// <summary>
    /// Builds a quest definition from a behavior instance, compiling its state
    /// dispatcher from the instance's runtime type. Used by
    /// <see cref="IQuestModule{T}"/> registrations so a single behavior-template
    /// class can be registered N times with different IDs / requirements / NPC
    /// bindings.
    /// </summary>
    /// <param name="id">Stable, unique quest id (persistence key).</param>
    /// <param name="name">Display name.</param>
    /// <param name="kind">Category; stored as its name.</param>
    /// <param name="npcKeys">NPC keys for npc-trigger routing (matched case-insensitively).</param>
    /// <param name="requirements">Requirements gating every dispatch.</param>
    /// <param name="instance">Behavior instance (typically a <see cref="QuestTemplateAttribute"/> class constructed with per-row data).</param>
    /// <returns>The definition, ready for <see cref="QuestRuntime{T}.Register"/>.</returns>
    /// <example>
    /// <code>
    /// public sealed class HuntTiers : IQuestModule&lt;MyQuestContext&gt;
    /// {
    ///     public void Register(QuestRuntime&lt;MyQuestContext&gt; runtime)
    ///     {
    ///         foreach (var tier in new[] { 1, 2, 3 })
    ///             runtime.Register(QuestDefinition&lt;MyQuestContext&gt;.Create(
    ///                 $"hunt.tier{tier}", $"Hunt {tier}", QuestKind.Side, Array.Empty&lt;string&gt;(),
    ///                 new[] { new QuestRequirement("level", QuestOperator.GreaterOrEqual, tier * 10) },
    ///                 new HuntTemplate(targetCount: tier * 5)));
    ///     }
    /// }
    /// </code>
    /// </example>
    public static QuestDefinition<TContext> Create(
        string id,
        string name,
        QuestKind kind,
        IReadOnlyList<string> npcKeys,
        IReadOnlyList<QuestRequirement> requirements,
        QuestBehavior<TContext> instance)
    {
        return new QuestDefinition<TContext>(
            id,
            name,
            kind.ToString(),
            npcKeys,
            requirements,
            instance,
            QuestStateDispatcher<TContext>.Compile(instance.GetType(), instance));
    }

    /// <summary>
    /// True when the quest can react to <paramref name="hookKey"/>: a hook interface handles it, any
    /// state handles it, or any state has a wildcard handler. Used by
    /// <see cref="QuestRuntime{T}.FireHookAsync"/> to pick targets.
    /// </summary>
    /// <param name="hookKey">Hook key.</param>
    /// <returns>Whether a handler might run.</returns>
    public bool HasHook(string hookKey) =>
        !string.IsNullOrWhiteSpace(hookKey)
        && (_hookHandlers.ContainsKey(hookKey)
            || StateDispatcher.HasHook(hookKey)
            || StateDispatcher.HasGenericStateHandlers);

    /// <summary>
    /// Dispatches a built-in trigger: converts it with <see cref="QuestTriggerToHookKey"/> and
    /// behaves like <see cref="DispatchHookAsync"/>. Does not evaluate requirements, persist or
    /// publish; use <see cref="QuestRuntime{T}"/> for that.
    /// </summary>
    /// <param name="context">Fully built dispatch context.</param>
    /// <param name="trigger">The trigger.</param>
    public async Task DispatchAsync(TContext context, QuestTrigger trigger)
    {
        var hookKey = QuestTriggerToHookKey(trigger);
        if (StateDispatcher.HasStateHandlers && await StateDispatcher.DispatchAsync(context, hookKey))
            return;

        await DispatchHookAsync(context, hookKey);
    }

    /// <summary>
    /// Runs the handler for <paramref name="hookKey"/>: first the state machine (current state's
    /// specific or wildcard handler, with transitions), and only if it handled nothing the
    /// hook-interface handler. Does not evaluate requirements, persist or publish.
    /// </summary>
    /// <param name="context">Fully built dispatch context.</param>
    /// <param name="hookKey">Hook key (built-in or custom).</param>
    public async Task DispatchHookAsync(TContext context, string hookKey)
    {
        if (StateDispatcher.HasStateHandlers && await StateDispatcher.DispatchAsync(context, hookKey))
            return;

        if (string.IsNullOrWhiteSpace(hookKey))
            return;

        if (_hookHandlers.TryGetValue(hookKey, out var handler))
            await handler(context);
    }

    /// <summary>Maps a built-in trigger to its <see cref="QuestHooks"/> key; <see cref="QuestTrigger.Custom"/> maps to an empty string.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The hook key.</returns>
    public static string QuestTriggerToHookKey(QuestTrigger trigger) => trigger switch
    {
        QuestTrigger.Enter => QuestHooks.Enter,
        QuestTrigger.Leave => QuestHooks.Leave,
        QuestTrigger.Level => QuestHooks.Level,
        QuestTrigger.Kill => QuestHooks.Kill,
        QuestTrigger.Npc => QuestHooks.Npc,
        QuestTrigger.Item => QuestHooks.Item,
        QuestTrigger.Button => QuestHooks.Button,
        QuestTrigger.Timer => QuestHooks.Timer,
        _ => string.Empty,
    };

    private static Dictionary<string, Func<TContext, Task>> CompileHookHandlers(QuestBehavior<TContext> instance)
    {
        var handlers = new Dictionary<string, Func<TContext, Task>>(StringComparer.OrdinalIgnoreCase);
        foreach (var iface in instance.GetType().GetInterfaces())
        {
            if (!IsQuestHookInterface(iface))
                continue;

            var hook = iface.GetCustomAttribute<QuestHookAttribute>(inherit: true);
            if (hook == null || string.IsNullOrWhiteSpace(hook.Key))
                continue;

            var method = iface.GetMethods()
                .SingleOrDefault(m =>
                    m.ReturnType == typeof(Task)
                    && m.GetParameters() is [{ } p]
                    && p.ParameterType.IsAssignableFrom(typeof(TContext)));

            if (method == null)
                continue;

            handlers[hook.Key] = ctx => (Task)method.Invoke(instance, [ctx])!;
        }

        return handlers;
    }

    private static bool IsQuestHookInterface(Type iface)
    {
        if (!iface.IsInterface)
            return false;

        return iface.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQuestHook<>));
    }
}
