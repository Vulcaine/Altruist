using Microsoft.AspNetCore.Builder;
using System.Net.WebSockets;
using Altruist.Transport;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Altruist.Authentication;
using Altruist.Contracts;
using Microsoft.Extensions.Logging;

namespace Altruist.Web;


public sealed class WebSocketTransport : ITransport
{
    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path)
    {
        UseWebSocketEndpoint(app, path, app.ApplicationServices.GetRequiredService<IConnectionManager>(), app.ApplicationServices);
    }

    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path)
    {
        UseWebSocketEndpoint(app, path, (app.ApplicationServices.GetRequiredService(type) as IConnectionManager)!, app.ApplicationServices);
    }

    private void  UseWebSocketEndpoint(
        IApplicationBuilder app, string path, IConnectionManager wsManager, IServiceProvider serviceProvider)
    {
        var shieldAttribute = wsManager.GetType().GetCustomAttribute<ShieldAttribute>();
        IShield shieldInstance = null!;

        if (shieldAttribute != null)
        {
            // shieldInstance = (IShield)serviceProvider.GetRequiredService(shieldAttribute.);
        }

        app.Use(async (context, next) =>
        {
            if (context.Request.Path == path && context.WebSockets.IsWebSocketRequest)
            {
                if (shieldInstance != null && !await shieldInstance.AuthenticateAsync(context))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                var clientId = Guid.NewGuid().ToString();
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var webSocketConnection = new WebSocketConnection(webSocket, clientId);
                await wsManager.HandleConnection(webSocketConnection, path, clientId);
            }
            else
            {
                await next();
            }
        });
    }
}

public sealed class WebSocketConfiguration : ITransportConfiguration
{
    public void Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ WebSocket support activated. Ready to transmit data across the cosmos in real-time! 🌌");
        services.AddSingleton<ITransport, WebSocketTransport>();

        services.AddSingleton<WebSocketConnectionSetup>();
        services.AddSingleton<ITransportConnectionSetupBase>(sp => sp.GetRequiredService<WebSocketConnectionSetup>());
        
        services.AddSingleton<ITransportConfiguration, WebSocketConfiguration>();
        services.AddSingleton<ITransportServiceToken, WebSocketTransportToken>();
    }
}

public sealed class WebSocketConnectionSetup : TransportConnectionSetup<WebSocketConnectionSetup>
{
    public WebSocketConnectionSetup(IServiceCollection services, IAltruistContext settings) : base(services, settings)
    {
    }
}

public sealed class WebSocketTransportToken : ITransportServiceToken
{
    public static WebSocketTransportToken Instance = new WebSocketTransportToken();
    public ITransportConfiguration Configuration => new WebSocketConfiguration();
}


public sealed class WebSocketConnection : IConnection
{
    private readonly WebSocket _webSocket;

    public string ConnectionId { get; }

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketConnection(WebSocket webSocket, string connectionId)
    {
        _webSocket = webSocket;
        ConnectionId = connectionId;
    }

    public async Task SendAsync(byte[] data)
    {
        if (IsConnected)
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        else
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return buffer.Take(result.Count).ToArray();
        }

        return Array.Empty<byte>();
    }

    public async Task CloseAsync()
    {
        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
    }
}

public class WebSocketTransportClient : ITransportClient
{
    private readonly ClientWebSocket _webSocket;

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public WebSocketTransportClient()
    {
        _webSocket = new ClientWebSocket();
    }

    public async Task ConnectAsync(string gatewayUrl)
    {
        try
        {
            await _webSocket.ConnectAsync(new Uri(gatewayUrl), CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error while connecting to WebSocket", ex);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal Closure", CancellationToken.None);
            _webSocket.Dispose();
        }
    }

    public async Task SendAsync(byte[] data)
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        else
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Text)
        {
            return buffer.Take(result.Count).ToArray();
        }

        return Array.Empty<byte>();
    }
}
