using Altruist;
using Altruist.Security;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;

namespace Portals;

[SessionShield]
public class SimpleGamePortal : AltruistGameSessionPortal<SpaceshipPlayer>
{
    public SimpleGamePortal(IPortalContext context, GameWorldCoordinator gameWorld, IPlayerService<SpaceshipPlayer> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }
}

[SessionShield]
public class RegenPortal : AltruistRegenPortal<SpaceshipPlayer>
{
    public RegenPortal(IPortalContext context, GameWorldCoordinator worldCoordinator, IPlayerService<SpaceshipPlayer> playerService, ILoggerFactory loggerFactory) : base(context, worldCoordinator, playerService, loggerFactory)
    {
    }

    public override Task<List<SpaceshipPlayer>> CalculateRegenOneFrame()
    {
        return Task.FromResult(new List<SpaceshipPlayer>());
    }
}