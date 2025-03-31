using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Altruist.ScyllaDB;
using Portals;

AltruistBuilder.Create(args)
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis()
    .WithScyllaDB()
    .StartServer();