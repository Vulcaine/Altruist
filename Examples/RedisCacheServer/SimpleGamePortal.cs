using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;

namespace Portals;

public class SimpleGamePortal : AltruistGamePortal<Spaceship>
{
    public SimpleGamePortal(IPortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
    }
}