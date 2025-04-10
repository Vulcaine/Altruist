using Altruist;
using Altruist.Redis;
using Altruist.ScyllaDB;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args, setup => setup.AddGamingSupport())
    .NoEngine()
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WithRedis(setup => setup.ForgeDocuments())
    .WithScyllaDB(setup => setup.CreateKeyspace<DefaultScyllaKeyspace>(
        setup => setup.ForgeVaults()
    ))
    .WebApp()
    .StartServer();