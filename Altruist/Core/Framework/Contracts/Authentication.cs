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

using System.Net;
using System.Security.Claims;

using Altruist.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Altruist;

/// <summary>Outcome of an <see cref="IShieldAuth.HandleAuthAsync"/> call: the authorization verdict plus, on success, who was authenticated.</summary>
/// <remarks>For MVC requests guarded by a <see cref="ShieldAttribute"/> it is stored in <c>HttpContext.Items["AuthResult"]</c>.</remarks>
public class AuthResult
{
    /// <summary>Whether authentication succeeded (<c>AuthorizationResult.Succeeded</c>).</summary>
    public AuthorizationResult AuthorizationResult { get; }
    /// <summary>Token, principal and expiry details on success; null on failure.</summary>
    public AuthDetails? AuthDetails { get; }

    /// <summary>Creates a result.</summary>
    /// <param name="authorizationResult">The verdict, e.g. <c>AuthorizationResult.Success()</c> or <c>AuthorizationResult.Failed()</c>.</param>
    /// <param name="authDetails">Details of the authenticated principal, or null on failure.</param>
    public AuthResult(AuthorizationResult authorizationResult, AuthDetails? authDetails)
    {
        AuthorizationResult = authorizationResult;
        AuthDetails = authDetails;
    }
}

/// <summary>Validates an access token string and returns its principal.</summary>
/// <remarks>In <c>altruist:security:mode: jwt</c> this resolves to the JWT validator (<see cref="IJwtTokenValidator"/>).
/// Inject it to validate tokens outside the shield pipeline (e.g. a custom handshake); to guard endpoints use a
/// <see cref="ShieldAttribute"/> subclass such as <see cref="JwtShieldAttribute"/>.</remarks>
public interface ITokenValidator
{
    /// <summary>Validates <paramref name="token"/>.</summary>
    /// <param name="token">The token; implementations may accept a <c>Bearer </c> prefix.</param>
    /// <returns>The principal, or null when invalid. Implementations may also throw for malformed tokens.</returns>
    Task<ClaimsPrincipal?> ValidateToken(string token);
}


/// <summary>Credentials of one incoming request or connection, passed to <see cref="IShieldAuth.HandleAuthAsync"/>.</summary>
/// <remarks><see cref="HttpAuthContext"/> for HTTP requests and WebSocket upgrades; <see cref="SocketAuthContext"/> (or your own type)
/// for raw TCP/UDP connections. Handlers inspect the concrete type to reach transport-specific data.</remarks>
public interface IAuthContext
{
    /// <summary>Client identifier supplied by the caller (may be empty). Self-declared by the client: never use it as an identity (the authenticated principal is in <see cref="AuthDetails"/>).</summary>
    public string ClientId { get; set; }
    /// <summary>The credential as presented: for HTTP the <c>Authorization</c> header without its <c>Bearer </c> scheme (the whole header for other schemes); empty when none.</summary>
    public string? Token { get; set; }
    /// <summary>Remote address; <see cref="IPAddress.None"/> when unknown.</summary>
    public IPAddress ClientIp { get; set; }
    /// <summary>When the request or connection arrived (UTC).</summary>
    public DateTime ConnectionTimestamp { get; set; }
}

/// <summary><see cref="IAuthContext"/> for non-HTTP (TCP/UDP) connections; the transport fills the properties.</summary>
/// <remarks>The built-in <see cref="JwtAuth"/> handler only supports <see cref="HttpAuthContext"/>, so socket connections need a custom <see cref="IShieldAuth"/>.</remarks>
public class SocketAuthContext : IAuthContext
{
    /// <inheritdoc/>
    public string ClientId { get; set; } = string.Empty;
    /// <inheritdoc/>
    public string? Token { get; set; } = string.Empty;
    /// <inheritdoc/>
    public IPAddress ClientIp { get; set; } = IPAddress.None;
    /// <inheritdoc/>
    public DateTime ConnectionTimestamp { get; set; }

}

/// <summary><see cref="IAuthContext"/> built from an HTTP request (also used for WebSocket upgrades).</summary>
public class HttpAuthContext : IAuthContext
{
    /// <inheritdoc/>
    public string ClientId { get; set; } = string.Empty;
    /// <inheritdoc/>
    public string? Token { get; set; } = string.Empty;
    /// <inheritdoc/>
    public IPAddress ClientIp { get; set; } = IPAddress.None;
    /// <inheritdoc/>
    public DateTime ConnectionTimestamp { get; set; }

    /// <summary>The underlying request; handlers may set <c>HttpContext.User</c> on success.</summary>
    public HttpContext HttpContext { get; set; }

    /// <summary>Reads <see cref="ClientId"/> from the <c>ClientId</c> header, <see cref="Token"/> from the <c>Authorization</c> header (without a <c>Bearer </c> scheme),
    /// <see cref="ClientIp"/> from the connection's remote address, and stamps <see cref="ConnectionTimestamp"/> with now (UTC).</summary>
    /// <param name="httpContext">The request.</param>
    public HttpAuthContext(HttpContext httpContext)
    {
        HttpContext = httpContext;

        ClientId = httpContext.Request.Headers["ClientId"].ToString();
        Token = CredentialOf(httpContext.Request.Headers.Authorization.ToString());

        ClientIp = httpContext.Connection.RemoteIpAddress ?? IPAddress.None;
        ConnectionTimestamp = DateTime.UtcNow;
    }

    private const string BearerScheme = "Bearer ";

    private static string CredentialOf(string authorization) =>
        authorization.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[BearerScheme.Length..].Trim()
            : authorization;
}



/// <summary>
/// An authentication handler run by <see cref="ShieldAttribute"/> for guarded MVC actions, WebSocket portals
/// and TCP/UDP connections.
/// </summary>
/// <remarks>
/// Built-ins: <see cref="JwtAuth"/> (behind <see cref="JwtShieldAttribute"/>) and the ticket handler. Implement your own for
/// API keys or custom schemes and expose it with a <see cref="ShieldAttribute"/> subclass; register it with
/// <c>[Service]</c> or let the shield build it with <c>ActivatorUtilities</c>. Return a failed result rather than throwing;
/// a thrown exception is treated as a failure anyway (fail closed).
/// </remarks>
/// <example>
/// <code>
/// public sealed class ApiKeyAuth : IShieldAuth
/// {
///     public Task&lt;AuthResult&gt; HandleAuthAsync(IAuthContext context) =&gt; Task.FromResult(
///         context is HttpAuthContext http &amp;&amp; http.HttpContext.Request.Headers["X-Api-Key"] == "secret"
///             ? new AuthResult(AuthorizationResult.Success(), null)
///             : new AuthResult(AuthorizationResult.Failed(), null));
/// }
/// public sealed class ApiKeyShieldAttribute : ShieldAttribute
/// {
///     public ApiKeyShieldAttribute() : base(typeof(ApiKeyAuth)) { }
/// }
/// </code>
/// </example>
public interface IShieldAuth
{
    /// <summary>Authenticates the request or connection described by <paramref name="context"/>.</summary>
    /// <param name="context">The credentials; check its concrete type for transport-specific data.</param>
    /// <returns>The verdict; include <see cref="AuthDetails"/> on success so portals know who the connection belongs to.</returns>
    Task<AuthResult> HandleAuthAsync(IAuthContext context);
}

/// <summary>A simple request guard returning allow/deny for an HTTP request.</summary>
/// <remarks>Not invoked anywhere by the framework at present; to guard endpoints use <see cref="ShieldAttribute"/> with an <see cref="IShieldAuth"/>.</remarks>
public interface IShield
{
    /// <summary>Returns whether <paramref name="context"/> is allowed.</summary>
    /// <param name="context">The request.</param>
    Task<bool> AuthenticateAsync(HttpContext context);
}
