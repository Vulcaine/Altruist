
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Represents a portal for managing regeneration updates for players in a real-time game system.
/// This class is responsible for calculating regeneration (such as health, mana, etc.) for players
/// and sending the updated information to the players in real-time.
/// </summary>
/// <typeparam name="TPlayerEntity">The type of the player entity.</typeparam>
/// <remarks>
/// The <see cref="AltruistRegenPortal{T}"/> class provides the core functionality for calculating 
/// and distributing regeneration updates to players, such as health and mana restoration.
/// The class supports real-time synchronization, where the updated player states are sent directly
/// to the players, ensuring they receive the latest changes immediately. 
/// 
/// It is designed to be used as a base class for more specific game systems that involve regeneration
/// mechanics. Derived classes are expected to implement the <see cref="CalculateRegen"/> method to 
/// define the actual regeneration logic and the list of players to be updated.
/// </remarks>
public abstract class AltruistRegenPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AltruistRegenPortal{T}"/> class with the specified context and logger factory.
    /// </summary>
    /// <param name="context">The context used for managing the portal's state and operations.</param>
    /// <param name="loggerFactory">The logger factory used for creating loggers.</param>
    protected AltruistRegenPortal(IPortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
    }

    /// <summary>
    /// Regenerates the player entities and sends real-time updates to the players for whom regeneration has occurred.
    /// </summary>
    /// <remarks>
    /// This method calculates the regeneration for players (such as health, mana, or other attributes),
    /// and sends updates to the players through real-time communication. The method uses the <see cref="CalculateRegen"/>
    /// method to retrieve the players requiring updates, and then synchronizes the updates via the router to 
    /// ensure the changes are sent to the players in real-time.
    /// </remarks>
    [Cycle()]
    public async virtual Task Regen()
    {
        var players = await CalculateRegen();
        var tasks = players.Select(Router.Synchronize.SendAsync).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Calculates and returns the list of player entities for which regeneration has occurred.
    /// </summary>
    /// <returns>
    /// A <see cref="Task"/> that represents the asynchronous operation. The task result contains
    /// a list of <see cref="PlayerEntity"/> objects, representing the players for whom regeneration 
    /// has been calculated and applied.
    /// </returns>
    /// <remarks>
    /// This method should be implemented to determine and return the specific set of players whose 
    /// regeneration state has been updated. The returned list will be used by the calling method 
    /// (e.g., <see cref="Regen()"/>) to synchronize or send updates to those players.
    /// </remarks>
    public abstract Task<List<TPlayerEntity>> CalculateRegen();
}

