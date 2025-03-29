using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args)
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis()
    .StartServer();