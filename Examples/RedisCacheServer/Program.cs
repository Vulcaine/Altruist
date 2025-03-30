using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args)
    .NoEngine()
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis(setup => setup.Index<Spaceship>())
    .StartServer();