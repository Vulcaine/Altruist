using Altruist;
using Altruist.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Web
{
    public static class AltruistWebExtensions
    {
        public static AltruistBuilder UseWebSocket(this AltruistBuilder builder)
        {
            builder.Services.AddSingleton<ITransport, WebSocketTransport>();
            builder.Services.AddSingleton<ITransportClient, WebSocketTransportClient>();
            return builder;
        }
    }
}