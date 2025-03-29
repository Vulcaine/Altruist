using Altruist;
using Altruist.Security;
using Altruist.Redis;
using Altruist.ScyllaDB;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args, setup => setup.AddGamingSupport())
    .EnableEngine(FrameRate.Hz30)
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game").MapPortal<RegenPortal>("/game").MapPortal<MyAuthPortal>("/game"))
    .WithRedis(setup => setup.ForgeDocuments())
    .WithScyllaDB(setup => setup.CreateKeyspace<DefaultScyllaKeyspace>(
        setup => setup.ForgeVaults()
    ))
    .WebApp(setup => setup.AddJwtAuth().StatefulToken<DefaultScyllaKeyspace>())
    .StartServer();