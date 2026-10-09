namespace Altruist.Gaming.Questing;

/// <summary>
/// Implemented by classes that register quest definitions at runtime instead of
/// relying on assembly-scan auto-discovery. Useful for data-driven quest sets
/// where one C# behavior class is shared across N tiers, each registered as a
/// distinct quest. Discovered automatically by
/// <see cref="QuestRuntime{T}.LoadModulesFromAssembly"/> after type-scan, which instantiates
/// it with its public parameterless constructor.
/// </summary>
/// <remarks>
/// Use attribute auto-discovery (<see cref="QuestAttribute"/>) for one-class-one-quest; use a
/// module when quests come from data or one <see cref="QuestTemplateAttribute"/> behavior backs
/// several quests. See <see cref="QuestDefinition{T}.Create"/> for an example.
/// </remarks>
/// <typeparam name="TContext">The game's quest context type.</typeparam>
public interface IQuestModule<TContext> where TContext : QuestContext
{
    /// <summary>Registers this module's quests, typically via <see cref="QuestRuntime{T}.Register"/> with <see cref="QuestDefinition{T}.Create"/>.</summary>
    /// <param name="runtime">The runtime being loaded.</param>
    void Register(QuestRuntime<TContext> runtime);
}
