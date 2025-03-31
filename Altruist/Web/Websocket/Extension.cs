namespace Altruist.Web;

public static class Extensions
{
    public static AltruistCacheBuilder WithWebsocket(this AltruistConnectionBuilder builder, Func<WebSocketConnectionSetup, WebSocketConnectionSetup> setup)
    {
        return builder.SetupTransport(WebSocketTransportToken.Instance, setup);
    }
}