using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;

namespace Portals;

public class SimpleGamePortal : AltruistGameSessionPortal<SpaceshipPlayer>
{
    public SimpleGamePortal(IPortalContext context, GameWorldCoordinator gameWorld, IPlayerService<SpaceshipPlayer> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }
}