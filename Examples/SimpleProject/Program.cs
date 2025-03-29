using Altruist;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args)
    .SetupWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .StartServer();