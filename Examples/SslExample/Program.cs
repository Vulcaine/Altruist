using Altruist;
using Altruist.Security;
using Altruist.Web;
using Microsoft.AspNetCore.Builder;
using Portals;

AltruistBuilder.Create(args, setup => setup.AddGamingSupport())
    .EnableEngine(FrameRate.Hz30)
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WebApp(setup => setup.EnableTls("certs/pfx"))
    .Configure(app =>
    {
        app.UseHttpsRedirection();
        return app;
    })
    // .WebApp()
    .StartServer();