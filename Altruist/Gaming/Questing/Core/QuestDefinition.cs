namespace Altruist.Gaming.Questing;

public sealed class QuestDefinition<TContext> where TContext : QuestContext
{
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
        HasLevelHook = Overrides(nameof(QuestBehavior<TContext>.OnLevel)) || stateDispatcher.HasStateHandlers;
    }

    public async Task DispatchAsync(TContext context, QuestTrigger trigger)
    {
        if (StateDispatcher.HasStateHandlers && await StateDispatcher.DispatchAsync(context))
            return;

        switch (trigger)
        {
            case QuestTrigger.Enter:
                await Instance.OnEnter(context);
                break;
            case QuestTrigger.Leave:
                await Instance.OnLeave(context);
                break;
            case QuestTrigger.Level:
                await Instance.OnLevel(context);
                break;
            case QuestTrigger.Kill:
                await Instance.OnKill(context);
                break;
            case QuestTrigger.Npc:
                await Instance.OnNpc(context);
                break;
            case QuestTrigger.Item:
                await Instance.OnItem(context);
                break;
            case QuestTrigger.Button:
                await Instance.OnButton(context);
                break;
            case QuestTrigger.Timer:
                await Instance.OnTimer(context);
                break;
            case QuestTrigger.Scroll:
                await Instance.OnScroll(context);
                break;
        }
    }

    private bool Overrides(string methodName)
    {
        var method = Instance.GetType().GetMethod(methodName);
        return method?.DeclaringType != null && method.DeclaringType != typeof(QuestBehavior<TContext>);
    }
}
