using System.Net.WebSockets;
using Altruist.Security;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Web;

/// <summary>
/// WebSocket transport that supports multiple routes (paths) but uses a single
/// IConnectionManager instance for handling connections. Each route can specify
/// an optional ShieldAttribute (authorization) which is enforced per-route.
/// </summary>
[Service(typeof(ITransport))]
[ConditionalOnConfig("altruist:transport:mode", havingValue: "websocket")]
public sealed class WebSocketTransport : ITransport
{
    private sealed record RouteInfo(string Path, Type? ShieldType);

    // path -> route meta (single manager, per-route shield)
    private readonly Dictionary<string, RouteInfo> _routes = new(StringComparer.Ordinal);

    /// <summary>
    /// Interface-mandated generic registration. If TType is a ShieldAttribute (or derives from it),
    /// the route becomes shielded with that attribute; otherwise it's registered as public.
    /// </summary>
    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path)
        where TType : class
    {
        var t = typeof(TType);
        var shieldType = typeof(ShieldAttribute).IsAssignableFrom(t) ? t : null;
        UseTransportEndpoints(app, path, shieldType);
    }

    /// <summary>
    /// Non-generic helper to register a public (unshielded) route.
    /// </summary>
    public void UseTransportEndpoints(IApplicationBuilder app, string path)
        => UseTransportEndpoints(app, path, shieldType: null);

    /// <summary>
    /// Non-generic helper to register a route with an explicit ShieldAttribute type.
    /// Pass null for unshielded routes.
    /// </summary>
    public void UseTransportEndpoints(IApplicationBuilder app, Type? shieldType, string path)
        => UseTransportEndpoints(app, path, shieldType);

    private void UseTransportEndpoints(IApplicationBuilder app, string path, Type? shieldType)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Route path cannot be null or whitespace.", nameof(path));

        if (shieldType is not null && !typeof(ShieldAttribute).IsAssignableFrom(shieldType))
            throw new ArgumentException($"shieldType must derive from {nameof(ShieldAttribute)}.", nameof(shieldType));

        _routes[path] = new RouteInfo(path, shieldType);
    }

    /// <summary>
    /// Adds the middleware that accepts WebSocket connections on registered routes,
    /// applies per-route shield (if any), and forwards the connection to the single
    /// IConnectionManager.HandleConnection(connection, @event, clientId).
    /// </summary>
    public void RouteTraffic(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.WebSockets.IsWebSocketRequest ||
                !_routes.TryGetValue(context.Request.Path, out var route))
            {
                await next();
                return;
            }

            // Resolve the single manager
            var sp = context.RequestServices;
            var manager = sp.GetRequiredService<IConnectionManager>();
            AuthDetails? authDetails = null;

            // Enforce route shield (if any)
            if (route.ShieldType is not null)
            {
                // Create a shield instance (attributes may depend on DI)
                var shield = (ShieldAttribute)ActivatorUtilities.CreateInstance(sp, route.ShieldType);

                var actionContext = new ActionContext(context, context.GetRouteData(), SharedActionDescriptor);
                var authorizationContext = new AuthorizationFilterContext(actionContext, SharedFilters);

                await shield.OnAuthorizationAsync(authorizationContext);

                if (authorizationContext.Result is UnauthorizedResult)
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                authDetails = (authorizationContext.HttpContext.Items["AuthResult"] as AuthResult)?.AuthDetails;
                if (authDetails is null)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }

            // Accept and hand off
            var clientId = Guid.NewGuid().ToString("N");
            var socket = await context.WebSockets.AcceptWebSocketAsync();

            var connection = new WebSocketConnection(socket, clientId, authDetails);
            await manager.HandleConnection(connection, context.Request.Path, clientId);
        });
    }

    // shared MVC bits for shield evaluation
    private static readonly ActionDescriptor SharedActionDescriptor = new();
    private static readonly List<IFilterMetadata> SharedFilters = new();
}


// ───────────────────────────────────────────────────────────────────────────
// Client (utility)
// ───────────────────────────────────────────────────────────────────────────

public interface ITransportClient
{
    Task ConnectAsync(string gatewayUrl);
    Task DisconnectAsync();
    Task SendAsync(byte[] data);
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken);
    bool IsConnected { get; }
}

public sealed class WebSocketTransportClient : ITransportClient
{
    private readonly ClientWebSocket _webSocket = new();

    public bool IsConnected => _webSocket.State == WebSocketState.Open;

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
        }
        _webSocket.Dispose();
    }

    public async Task SendAsync(byte[] data)
    {
        if (!IsConnected) throw new InvalidOperationException("WebSocket is not connected.");
        await _webSocket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await DisconnectAsync();
            return Array.Empty<byte>();
        }

        return buffer.Take(result.Count).ToArray();
    }
}