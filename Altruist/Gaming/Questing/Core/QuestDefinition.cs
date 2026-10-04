using System.Reflection;

namespace Altruist.Gaming.Questing;

public sealed class QuestDefinition<TContext> where TContext : QuestContext
{
    private readonly Dictionary<string, Func<TContext, Task>> _hookHandlers;

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public IReadOnlyList<string> NpcKeys { get; }
    public IReadOnlyList<QuestRequirement> Requirements { get; }
    public QuestBehavior<TContext> Instance { get; }
    public QuestStateDispatcher<TContext> StateDispatcher { get; }
    public bool HasLevelHook { get; }

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

    public bool HasHook(string hookKey) =>
        !string.IsNullOrWhiteSpace(hookKey)
        && (_hookHandlers.ContainsKey(hookKey)
            || StateDispatcher.HasHook(hookKey)
            || StateDispatcher.HasGenericStateHandlers);

    public async Task DispatchAsync(TContext context, QuestTrigger trigger)
    {
        var hookKey = QuestTriggerToHookKey(trigger);
        if (StateDispatcher.HasStateHandlers && await StateDispatcher.DispatchAsync(context, hookKey))
            return;

        await DispatchHookAsync(context, hookKey);
    }

    public async Task DispatchHookAsync(TContext context, string hookKey)
    {
        if (StateDispatcher.HasStateHandlers && await StateDispatcher.DispatchAsync(context, hookKey))
            return;

        if (string.IsNullOrWhiteSpace(hookKey))
            return;

        if (_hookHandlers.TryGetValue(hookKey, out var handler))
            await handler(context);
    }

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
