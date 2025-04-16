using Microsoft.AspNetCore.Builder;
using System.Net.WebSockets;
using Altruist.Transport;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Altruist.Security;
using Altruist.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Abstractions;
using System.Text.Json.Serialization;

namespace Altruist.Web;

public sealed class WebSocketTransport : ITransport
{
    private readonly Dictionary<string, List<(IConnectionManager Manager, ShieldAttribute? Shield)>> _routes = new();

    public void RegisterConnectionManager(IConnectionManager manager, string path)
    {
        if (!_routes.ContainsKey(path))
        {
            _routes[path] = new List<(IConnectionManager, ShieldAttribute?)>();
        }

        var shield = manager.GetType().GetCustomAttribute<ShieldAttribute>();
        _routes[path].Add((manager, shield));
    }

    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path)
    {
        var manager = app.ApplicationServices.GetRequiredService<IConnectionManager>();
        RegisterConnectionManager(manager, path);
    }

    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path)
    {
        var manager = (IConnectionManager)app.ApplicationServices.GetRequiredService(type);
        RegisterConnectionManager(manager, path);
    }

    public void RouteTraffic(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !_routes.TryGetValue(context.Request.Path, out var managers))
            {
                await next();
                return;
            }

            var allowedManagers = new List<(IConnectionManager Manager, AuthDetails? AuthDetails)>();

            foreach (var (manager, shield) in managers)
            {
                AuthDetails? authDetails = null;

                if (shield != null)
                {
                    var actionContext = new ActionContext(context, context.GetRouteData(), SharedActionDescriptor);
                    var authorizationContext = new AuthorizationFilterContext(actionContext, SharedFilters);

                    await shield.OnAuthorizationAsync(authorizationContext);

                    if (authorizationContext.Result is UnauthorizedResult)
                    {
                        continue; // Skip this manager
                    }

                    authDetails = (authorizationContext.HttpContext.Items["AuthResult"] as AuthResult)?.AuthDetails;

                    if (authDetails == null)
                    {
                        continue; // Still not authorized
                    }
                }

                allowedManagers.Add((manager, authDetails));
            }

            if (allowedManagers.Count == 0)
            {
                context.Response.StatusCode = 401;
                return;
            }

            var clientId = Guid.NewGuid().ToString();
            var socket = await context.WebSockets.AcceptWebSocketAsync();

            foreach (var (manager, authDetails) in allowedManagers)
            {
                var connection = new WebSocketConnection(socket, clientId, authDetails);
                await manager.HandleConnection(connection, context.Request.Path, clientId);
            }
        });
    }

    private static readonly ActionDescriptor SharedActionDescriptor = new();
    private static readonly List<IFilterMetadata> SharedFilters = new();
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
    public ITransportConfiguration Configuration { get; } = new WebSocketConfiguration();

    public string Description => "📡 Transport: WebSocket";
}

public sealed class CachedWebSocketConnection : Connection
{
    [JsonIgnore]
    private WebSocketConnection? _connection;

    public CachedWebSocketConnection(WebSocketConnection connection)
    {
        _connection = connection;
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    public CachedWebSocketConnection(Connection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    public override async Task SendAsync(byte[] data)
    {
        if (_connection != null)
        {
            await _connection.SendAsync(data);
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            return await _connection.ReceiveAsync(cancellationToken);
        }
        else
        {
            return Array.Empty<byte>();
        }
    }

    public override Task CloseAsync()
    {
        if (_connection != null)
        {
            return _connection.CloseAsync();
        }
        else
        {
            return Task.CompletedTask;
        }
    }
}

public sealed class WebSocketConnection : Connection
{
    [JsonIgnore]
    private readonly WebSocket? _webSocket;

    [JsonPropertyName("IsConnected")]
    public override bool IsConnected => _webSocket != null && _webSocket.State == WebSocketState.Open;

    public WebSocketConnection() { } // for json deserialization

    public WebSocketConnection(WebSocket webSocket, string connectionId, AuthDetails? authDetails)
    {
        _webSocket = webSocket;
        ConnectionId = connectionId;
        AuthDetails = authDetails;
    }

    public override async Task SendAsync(byte[] data)
    {
        if (IsConnected)
        {
            await _webSocket!.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        else
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            var buffer = new byte[1024];
            var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                return buffer.Take(result.Count).ToArray();
            }
        }

        return Array.Empty<byte>();
    }

    public override async Task CloseAsync()
    {
        if (IsConnected)
        {
            await _webSocket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
        }
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
