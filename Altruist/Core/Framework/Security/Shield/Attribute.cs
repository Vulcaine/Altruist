/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Security;

/// <summary>
/// Guards an MVC controller/action or a portal with an <see cref="IShieldAuth"/> handler: the request (or
/// WebSocket upgrade) gets 401 unless the handler succeeds. On success the <see cref="AuthResult"/> is stored in
/// <c>HttpContext.Items["AuthResult"]</c>. Fails closed: a handler that cannot be resolved or throws denies.
/// </summary>
/// <remarks>
/// Use a ready-made subclass rather than this one directly: <see cref="JwtShieldAttribute"/> for
/// <c>Authorization: Bearer</c> JWTs, <see cref="TicketShieldAttribute"/> for WebSocket connections opened with a
/// one-time ticket (browsers cannot send headers on WebSockets), <see cref="SessionShieldAttribute"/> for
/// <c>altruist:security:mode: session</c>. Derive your own (passing your handler type) for custom schemes; the
/// handler is resolved from DI by its type, or built with <c>ActivatorUtilities</c> when it is only registered as
/// <see cref="IShieldAuth"/>. The parameterless constructor installs no handler and lets every request through.
/// </remarks>
/// <example>
/// <code>
/// public sealed class ApiKeyShieldAttribute : ShieldAttribute
/// {
///     public ApiKeyShieldAttribute() : base(typeof(ApiKeyAuth)) { } // ApiKeyAuth : IShieldAuth
/// }
///
/// [ApiKeyShield]
/// public class AdminController : ControllerBase { }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class ShieldAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly Type? _authHandlerType;

    /// <summary>A shield without a handler: authorization always passes. Prefer a subclass that passes a handler type.</summary>
    public ShieldAttribute() { }

    /// <summary>A shield that authenticates with <paramref name="authHandlerType"/> (an <see cref="IShieldAuth"/> implementation).</summary>
    public ShieldAttribute(Type authHandlerType)
    {
        _authHandlerType = authHandlerType;
    }

    // HTTP-based authentication for MVC + WebSockets. Fails closed: a handler that cannot be
    // resolved or that throws denies the request (previously both silently let it through).
    /// <summary>
    /// MVC authorization filter: runs the handler with an <see cref="HttpAuthContext"/>; sets a 401 result when it
    /// fails, throws or cannot be resolved. Called by ASP.NET, not by user code.
    /// </summary>
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (_authHandlerType is null)
            return;

        var authHandler = ResolveHandler(context.HttpContext.RequestServices);
        if (authHandler is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        try
        {
            var result = await authHandler.HandleAuthAsync(new HttpAuthContext(context.HttpContext));

            context.HttpContext.Items["AuthResult"] = result;

            if (!result.AuthorizationResult.Succeeded)
            {
                context.Result = new UnauthorizedResult();
            }
        }
        catch (Exception)
        {
            context.HttpContext.Items.Remove("AuthResult");
            context.Result = new UnauthorizedResult();
        }
    }

    private IShieldAuth? ResolveHandler(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(_authHandlerType!) is IShieldAuth registered)
            return registered;

        // Handlers are usually registered under IShieldAuth only; build the concrete type.
        try
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, _authHandlerType!) as IShieldAuth;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Non-HTTP authentication (for TCP/UDP)
    /// <summary>
    /// Runs the handler for a non-HTTP connection (TCP/UDP transports). Returns the <see cref="AuthDetails"/> on
    /// success, or null when there is no handler, it cannot be resolved, it fails or it throws. Note that the
    /// built-in <see cref="JwtAuth"/> and <see cref="TicketShieldAuth"/> handlers only accept <see cref="HttpAuthContext"/>,
    /// so they always yield null here.
    /// </summary>
    /// <param name="serviceProvider">Provider the handler is resolved from.</param>
    /// <param name="context">The connection's credentials.</param>
    public async Task<AuthDetails?> AuthenticateNonHttpAsync(IServiceProvider serviceProvider, IAuthContext context)
    {
        if (_authHandlerType != null)
        {
            var authHandler = ResolveHandler(serviceProvider);
            if (authHandler != null)
            {
                try
                {
                    var result = await authHandler.HandleAuthAsync(context);
                    return result.AuthorizationResult.Succeeded ? result.AuthDetails : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
        return null;
    }
}
