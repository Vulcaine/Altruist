
using Altruist.Security;
using Altruist.Transport;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http; 
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
[ConditionalOnConfig("altruist:server:transport:mode", havingValue: "websocket")]
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
        => UseTransportEndpoints(app, path, serviceType: null);

    /// <summary>
    /// Non-generic helper to register a route with an explicit ShieldAttribute type.
    /// Pass null for unshielded routes.
    /// </summary>
    public void UseTransportEndpoints(IApplicationBuilder app, Type? serviceType, string path)
        => UseTransportEndpoints(app, path, serviceType);

    private void UseTransportEndpoints(IApplicationBuilder app, string path, Type? serviceType)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Route path cannot be null or whitespace.", nameof(path));

        Type? shieldType = null;

        if (serviceType is not null)
        {
            var shieldAttr = serviceType
                .GetCustomAttributes(inherit: true)
                .OfType<ShieldAttribute>()
                .FirstOrDefault();

            if (shieldAttr is not null)
            {
                shieldType = shieldAttr.GetType();
            }
        }

        if (shieldType is not null && !typeof(ShieldAttribute).IsAssignableFrom(shieldType))
        {
            shieldType = null;
        }

        if (_routes.TryGetValue(path, out var existing))
        {
            var effectiveShield = existing.ShieldType ?? shieldType;
            _routes[path] = existing with { ShieldType = effectiveShield };
        }
        else
        {
            _routes[path] = new RouteInfo(path, shieldType);
        }
    }

    /// <summary>
    /// Adds the middleware that:
    ///  - for ANY request with a registered route, runs the per-route shield (if any)
    ///  - for WebSocket requests, accepts and forwards to IConnectionManager
    ///  - for normal HTTP requests, just lets the pipeline continue after shield
    /// </summary>
    public void RouteTraffic(IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            // Try to find a RouteInfo for this path
            if (!_routes.TryGetValue(context.Request.Path, out var route))
            {
                // Not registered yet: try to infer Shield from endpoint metadata (e.g. [JwtShield] on controller)
                var endpoint = context.GetEndpoint();
                var shieldAttr = endpoint?.Metadata.GetMetadata<ShieldAttribute>();

                if (shieldAttr is not null)
                {
                    // Register this path with the ShieldAttribute's type
                    route = new RouteInfo(context.Request.Path, shieldAttr.GetType());
                    _routes[route.Path] = route;
                }
                else
                {
                    // No route info and no shield metadata: just continue as normal
                    await next();
                    return;
                }
            }

            var sp = context.RequestServices;
            AuthDetails? authDetails = null;

            // Enforce route shield (if any) FOR BOTH HTTP + WEBSOCKET
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

            // If this is NOT a WebSocket request: shield ran, user is authenticated, continue to MVC
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await next();
                return;
            }

            // WebSocket path: accept and hand off
            var manager = sp.GetRequiredService<IConnectionManager>();
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
