using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;

namespace Portals;

public class SimpleGamePortal : AltruistGamePortal<Spaceship>
{
    public SimpleGamePortal(
        IPortalContext context,
        GameWorldCoordinator coordinator,
        IPlayerService<Spaceship> playerService,
        ILoggerFactory loggerFactory)
        : base(context, coordinator, playerService, loggerFactory)
    {
    }
}