using Altruist.Contracts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Web;

[Service(typeof(ITransportServiceToken))]
[ConditionalOnConfig("altruist:server:transport:mode", havingValue: "websocket")]
public sealed class WebSocketTransportToken : ITransportServiceToken
{
    public static WebSocketTransportToken Instance = new WebSocketTransportToken();
    public ITransportConfiguration Configuration { get; } = new WebSocketConfiguration();

    public string Description => "📡 Transport: WebSocket";
}


[Service(typeof(ITransportConfiguration))]
[ConditionalOnConfig("altruist:server:transport:mode", havingValue: "websocket")]
public sealed class WebSocketConfiguration : ITransportConfiguration
{
    public Task Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ WebSocket support activated. Ready to transmit data across the cosmos in real-time! 🌌");
        return Task.CompletedTask;
    }
}
