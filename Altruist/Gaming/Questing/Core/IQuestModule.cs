namespace Altruist.Gaming.Questing;

/// <summary>
/// Implemented by classes that register quest definitions at runtime instead of
/// relying on assembly-scan auto-discovery. Useful for data-driven quest sets
/// where one C# behavior class is shared across N tiers, each registered as a
/// distinct quest. Discovered automatically by
/// <see cref="QuestRuntime{T}.LoadModulesFromAssembly"/> after type-scan.
/// </summary>
public interface IQuestModule<TContext> where TContext : QuestContext
{
    void Register(QuestRuntime<TContext> runtime);
}
