using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args)
    .NoEngine()
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis(setup => setup.AddDocument<Connection>().AddDocument<WebSocketConnection>().AddDocument<Spaceship>().AddDocument<RoomPacket>())
    .WebApp()
    .StartServer();