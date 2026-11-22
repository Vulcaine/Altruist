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

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

/// <summary>
/// The <see cref="IShieldAuth"/> behind <see cref="JwtShieldAttribute"/> (registered with <c>altruist:security:mode: jwt</c>).
/// Reads the bearer JWT (an optional <c>;jwt</c> suffix is ignored), validates it with <see cref="IJwtTokenValidator"/>,
/// sets <c>HttpContext.User</c> for HTTP and returns <see cref="AuthDetails"/> whose principal is the <c>sub</c> claim and
/// whose lifetime is the token's.
/// </summary>
/// <remarks>
/// The token comes from <c>Authorization: Bearer</c> for an <see cref="HttpAuthContext"/> (requests and WebSocket upgrades)
/// and from <see cref="IAuthContext.Token"/> for other contexts (a socket transport that received a credential). The
/// details' IP is the token's <c>Ip</c> claim, or the connection's address when the token has none; the group key is the
/// <c>GroupKey</c> claim, or the principal id when the token has none. So tokens of <see cref="IAccessTokenIssuer"/>
/// (which carry neither) yield live details.
/// </remarks>
[Service(typeof(IShieldAuth))]
[ConditionalOnConfig("altruist:security")]
[ConditionalOnConfig("altruist:security:mode", havingValue: "jwt")]
public class JwtAuth : IShieldAuth
{
    private readonly IJwtTokenValidator _tokenValidator;
    private readonly TokenSessionSyncService? _syncService;

    /// <summary>Creates the handler (resolved by DI).</summary>
    /// <param name="tokenValidator">Validates the bearer token.</param>
    /// <param name="serviceProvider">Used to resolve the optional <see cref="TokenSessionSyncService"/>.</param>
    public JwtAuth(IJwtTokenValidator tokenValidator, IServiceProvider serviceProvider)
    {
        _tokenValidator = tokenValidator;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
    }

    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    /// <summary>Authenticates the bearer token of <paramref name="context"/>; fails (no exception) when it is missing, malformed or invalid.</summary>
    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = GetTokenFromRequest(context);

        if (string.IsNullOrWhiteSpace(token))
        {
            return new AuthResult(AuthorizationResult.Failed(), null!);
        }

        // Validate before reading any claims: malformed input must yield 401, not an exception.
        ClaimsPrincipal? principal;
        try
        {
            principal = await _tokenValidator.ValidateToken(token);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return new AuthResult(AuthorizationResult.Failed(), null!);
        }

        if (principal == null)
        {
            return new AuthResult(AuthorizationResult.Failed(), null!);
        }

        if (context is HttpAuthContext httpAuthContext)
        {
            httpAuthContext.HttpContext.User = principal;
        }

        var authDetails = ExtractAuthDetails(token, context);
        return new AuthResult(AuthorizationResult.Success(), authDetails);
    }

    private static string GetTokenFromRequest(IAuthContext context)
    {
        var credential = context is HttpAuthContext httpAuthContext
            ? httpAuthContext.HttpContext.Request.Headers["Authorization"].ToString()
            : context.Token ?? "";
        if (credential.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            credential = credential["Bearer ".Length..];
        return credential.Trim().Split(";")[0];
    }

    private AuthDetails ExtractAuthDetails(string token, IAuthContext context)
    {
        var jwt = _tokenHandler.ReadJwtToken(token);
        var expClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);

        if (expClaim == null || !long.TryParse(expClaim.Value, out long expUnix))
        {
            throw new UnauthorizedAccessException("Invalid JWT: Missing or malformed expiration claim.");
        }

        var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        var remainingTime = expirationTime - DateTimeOffset.UtcNow;
        var principalId = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value ?? "Unknown";
        var ip = jwt.Claims.FirstOrDefault(c => c.Type == "Ip")?.Value ?? context.ClientIp.ToString();
        var groupKey = jwt.Claims.FirstOrDefault(c => c.Type == "GroupKey")?.Value ?? principalId;

        return new AuthDetails(token, principalId, ip, groupKey, remainingTime);
    }
}

/// <summary>Validates JWTs against the parameters registered by <see cref="AuthConfiguration"/>. Inject it when you need the JWT validator specifically; <see cref="ITokenValidator"/> resolves to the same implementation in jwt mode.</summary>
public interface IJwtTokenValidator : ITokenValidator
{

}

/// <summary>
/// Default <see cref="IJwtTokenValidator"/> and <see cref="ITokenValidator"/> in <c>altruist:security:mode: jwt</c>:
/// validates signature (HS256), issuer, audience and lifetime (no clock skew) with the registered
/// <see cref="TokenValidationParameters"/>.
/// </summary>
[Service(typeof(ITokenValidator))]
[Service(typeof(IJwtTokenValidator))]
[ConditionalOnConfig("altruist:security")]
[ConditionalOnConfig("altruist:security:mode", havingValue: "jwt")]
public class JwtTokenValidator : IJwtTokenValidator
{
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParams;

    /// <summary>Creates the validator over <paramref name="parameters"/>.</summary>
    public JwtTokenValidator(TokenValidationParameters parameters)
    {
        _tokenHandler = new JwtSecurityTokenHandler();
        _validationParams = parameters;
    }

    /// <summary>
    /// Removes any <c>;jwt</c> suffix and validates the token. Returns the principal (inbound claims mapped by
    /// <see cref="JwtSecurityTokenHandler"/>'s defaults, so <c>sub</c> appears as <see cref="ClaimTypes.NameIdentifier"/>).
    /// </summary>
    /// <exception cref="SecurityTokenException">When the token is invalid or expired (it never returns null).</exception>
    /// <exception cref="ArgumentException">When the token is malformed.</exception>
    public Task<ClaimsPrincipal?> ValidateToken(string token)
    {
        string actualToken = token.Replace(";jwt", "");
        return Task.FromResult(_tokenHandler.ValidateToken(actualToken, _validationParams, out _))!;
    }
}


/// <summary>
/// Requires a valid JWT in <c>Authorization: Bearer ...</c> (via <see cref="JwtAuth"/>); otherwise 401. Needs
/// <c>altruist:security:mode: jwt</c> (the default). Use on controllers, actions and portals reached over HTTP or a
/// WebSocket upgrade by clients that can send headers; browsers opening WebSockets should use
/// <see cref="TicketShieldAttribute"/> instead. Plain ASP.NET <c>[Authorize]</c> works too for MVC.
/// </summary>
/// <example>
/// <code>
/// [JwtShield]
/// [HttpGet("me")]
/// public IActionResult Me() =&gt; Ok(User.PrincipalId());
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class JwtShieldAttribute : ShieldAttribute
{
    /// <summary>Creates the shield.</summary>
    public JwtShieldAttribute() : base(typeof(JwtAuth)) { }
}
