using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebSockets;

namespace Altruist.Web;

public static class WebExtensions
{
    public static IAfterConnectionBuilder WithWebsocket(this AltruistConnectionBuilder builder, Func<WebSocketConnectionSetup, WebSocketConnectionSetup> setup, Action<WebSocketOptions>? configure = null)
    {
        builder.Services.AddWebSockets(configure ?? (configure => { }));
        return builder.SetupTransport(WebSocketTransportToken.Instance, setup);
    }
}