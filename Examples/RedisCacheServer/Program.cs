using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args, serviceBuilder => serviceBuilder.AddGamingSupport())
    .NoEngine()
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis(setup => setup.ForgeDocuments())
    .WebApp()
    .StartServer();