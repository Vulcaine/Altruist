using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Web;

[Service(typeof(ITransportServiceToken))]
[ConditionalOnConfig("altruist:server:transport:websocket:enabled", havingValue: "true")]
public sealed class WebSocketTransportToken : ITransportServiceToken
{
    public static WebSocketTransportToken Instance = new WebSocketTransportToken();

    public string Description => "📡 Transport: WebSocket";
}


[Service(typeof(ITransportConfiguration))]
[ConditionalOnConfig("altruist:server:transport:websocket:enabled", havingValue: "true")]
public sealed class WebSocketConfiguration : ITransportConfiguration
{
    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("WebSocket support activated.");
        return Task.CompletedTask;
    }
}
