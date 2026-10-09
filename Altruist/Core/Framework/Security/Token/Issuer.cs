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

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

/// <summary>
/// Legacy token-pair issuer used by <see cref="AuthController"/> and <see cref="IAuthService"/>: returns an
/// <see cref="IIssue"/> (a <see cref="TokenIssue"/> with access and refresh token).
/// </summary>
/// <remarks>
/// Choosing a token kind: for HTTP APIs prefer <see cref="IAccessTokenIssuer"/> (short-lived stateless JWT, lifetime
/// from <c>altruist:security:access-token-minutes</c>) together with <see cref="IRefreshTokenService"/> (rotating,
/// revocable opaque refresh tokens, usually in a <see cref="RefreshCookie"/>). For WebSocket connections use
/// <see cref="IConnectionTicketService"/>. For email links and resets use <see cref="IOneTimeTokenStore"/>.
/// <see cref="IJwtTokenIssuer"/> / <see cref="ISessionTokenIssuer"/> remain for the
/// <see cref="Altruist.Security.Http.JwtAuthController"/> and session-mode flows.
/// </remarks>
public interface IIssuer
{
    /// <summary>Issues a new token pair.</summary>
    IIssue Issue();
}


/// <summary>Names of the built-in token kinds (the <see cref="TokenIssue.Type"/> values).</summary>
public static class IssuerKeys
{
    /// <summary>Opaque session tokens from <see cref="SessionTokenIssuer"/>.</summary>
    public const string SessionToken = "SessionToken";
    /// <summary>JWTs from <see cref="JwtTokenIssuer"/>.</summary>
    public const string JwtToken = "JwtToken";
}

/// <summary>A pair of opaque session tokens (<c>GUID;session</c>) from <see cref="SessionTokenIssuer"/>; valid only while stored in <see cref="TokenSessionSyncService"/>.</summary>
public class SessionToken : TokenIssue
{
    /// <inheritdoc/>
    public override string Type { get; set; } = "SessionToken";
}

/// <summary>A pair of signed JWTs (<c>&lt;jwt&gt;;jwt</c>) from <see cref="JwtTokenIssuer"/>.</summary>
public class JwtToken : TokenIssue
{
    /// <inheritdoc/>
    public override string Type { get; set; } = "JwtToken";
}

/// <summary>
/// Issues opaque <see cref="SessionToken"/> pairs (random GUIDs, server-side state). Used by
/// <see cref="IAuthService.Upgrade"/> and <c>altruist:security:mode: session</c>; the tokens mean nothing until saved
/// in <see cref="TokenSessionSyncService"/>. Registered as a singleton when <c>altruist:security</c> exists.
/// </summary>
public interface ISessionTokenIssuer : IIssuer
{

}

/// <summary>
/// Default <see cref="ISessionTokenIssuer"/>: access tokens valid 1 hour and refresh tokens 7 days unless
/// other lifetimes are passed to the constructor.
/// </summary>
[Service(typeof(ISessionTokenIssuer), DependsOn = new[] { typeof(AuthConfiguration) })]
[ConditionalOnConfig("altruist:security")]
public class SessionTokenIssuer : ISessionTokenIssuer
{
    private readonly TimeSpan _accessTokenExpiration;
    private readonly TimeSpan _refreshTokenExpiration;
    /// <summary>Creates the issuer.</summary>
    /// <param name="accessTokenExpiration">Access-token lifetime (default 1 hour).</param>
    /// <param name="refreshTokenExpiration">Refresh-token lifetime (default 7 days).</param>
    public SessionTokenIssuer(TimeSpan? accessTokenExpiration = null, TimeSpan? refreshTokenExpiration = null)
    {
        _accessTokenExpiration = accessTokenExpiration ?? TimeSpan.FromHours(1);
        _refreshTokenExpiration = refreshTokenExpiration ?? TimeSpan.FromDays(7);
    }

    /// <summary>Issues a new <see cref="SessionToken"/> with random <c>GUID;session</c> tokens and UTC expirations.</summary>
    public IIssue Issue()
    {
        return new SessionToken
        {
            AccessToken = Guid.NewGuid().ToString() + ";session",
            RefreshToken = Guid.NewGuid().ToString() + ";session",
            RefreshExpiration = DateTime.UtcNow + _refreshTokenExpiration,
            AccessExpiration = DateTime.UtcNow + _accessTokenExpiration
        };
    }
}

/// <summary>
/// Issues HS256 <see cref="JwtToken"/> pairs signed with <c>altruist:security:key</c> (issuer and audience
/// <c>"Altruist"</c>). Used by <see cref="Altruist.Security.Http.JwtAuthController"/>. For new code prefer
/// <see cref="IAccessTokenIssuer"/> + <see cref="IRefreshTokenService"/>: configurable lifetime, per-call claims and
/// revocable refresh tokens.
/// </summary>
public interface IJwtTokenIssuer : IIssuer
{

}

/// <summary>
/// Default <see cref="IJwtTokenIssuer"/> (singleton when <c>altruist:security</c> exists). The access JWT lives
/// <c>altruist:security:jwt:access-token-minutes</c> (default 60) and the refresh JWT
/// <c>altruist:security:jwt:refresh-token-minutes</c> (default 10080, 7 days; <see cref="SetRefreshTokenExpiry"/> overrides
/// it per copy). Both carry the same claims plus a random <c>jti</c> and get a <c>;jwt</c> suffix; the issued
/// <see cref="TokenIssue.AccessExpiration"/> and <see cref="TokenIssue.RefreshExpiration"/> match their <c>exp</c>. Call
/// <see cref="WithClaims"/> per request: it returns a copy, so the shared singleton is never mutated with a caller's claims.
/// </summary>
[Service(typeof(IJwtTokenIssuer), DependsOn = new[] { typeof(AuthConfiguration) })]
[ConditionalOnConfig("altruist:security")]
public class JwtTokenIssuer : IJwtTokenIssuer
{
    /// <summary>Config key of the access JWT lifetime in minutes (default 60).</summary>
    public const string AccessLifetimeKey = "altruist:security:jwt:access-token-minutes";
    /// <summary>Config key of the refresh JWT lifetime in minutes (default 10080 = 7 days).</summary>
    public const string RefreshLifetimeKey = "altruist:security:jwt:refresh-token-minutes";

    /// <summary>Default access JWT lifetime (1 hour).</summary>
    public static readonly TimeSpan DefaultAccessLifetime = TimeSpan.FromHours(1);
    /// <summary>Default refresh JWT lifetime (7 days).</summary>
    public static readonly TimeSpan DefaultRefreshLifetime = TimeSpan.FromDays(7);

    /// <summary>The JWT bearer options of the default scheme; supplies the signing key, issuer and audience.</summary>
    public JwtBearerOptions JwtOptions { get; }
    private readonly TimeSpan _accessTokenExpiry;
    private IEnumerable<Claim>? _customClaims;
    private TimeSpan _refreshTokenExpiry;

    /// <summary>Creates the issuer from the registered <see cref="JwtBearerDefaults.AuthenticationScheme"/> options with the default lifetimes.</summary>
    /// <param name="jwtOptions">The JWT bearer options monitor.</param>
    public JwtTokenIssuer(IOptionsMonitor<JwtBearerOptions> jwtOptions)
        : this(jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme), DefaultAccessLifetime, DefaultRefreshLifetime) { }

    /// <summary>DI constructor: lifetimes from <see cref="AccessLifetimeKey"/> and <see cref="RefreshLifetimeKey"/>.</summary>
    /// <param name="jwtOptions">The JWT bearer options monitor.</param>
    /// <param name="accessTokenMinutes">Access JWT lifetime in minutes; must be positive.</param>
    /// <param name="refreshTokenMinutes">Refresh JWT lifetime in minutes; must be longer than the access lifetime.</param>
    /// <exception cref="ArgumentOutOfRangeException">When a lifetime is not positive or the refresh lifetime is not longer than the access lifetime.</exception>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public JwtTokenIssuer(
        IOptionsMonitor<JwtBearerOptions> jwtOptions,
        [AppConfigValue(AccessLifetimeKey, "60")] double accessTokenMinutes,
        [AppConfigValue(RefreshLifetimeKey, "10080")] double refreshTokenMinutes)
        : this(jwtOptions.Get(JwtBearerDefaults.AuthenticationScheme), TimeSpan.FromMinutes(accessTokenMinutes), TimeSpan.FromMinutes(refreshTokenMinutes)) { }

    private JwtTokenIssuer(JwtBearerOptions jwtOptions, TimeSpan accessTokenExpiry, TimeSpan refreshTokenExpiry)
    {
        if (accessTokenExpiry <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(accessTokenExpiry), accessTokenExpiry, $"{AccessLifetimeKey} must be positive.");
        if (refreshTokenExpiry <= accessTokenExpiry)
            throw new ArgumentOutOfRangeException(nameof(refreshTokenExpiry), refreshTokenExpiry, $"{RefreshLifetimeKey} must be longer than {AccessLifetimeKey}.");
        JwtOptions = jwtOptions;
        _accessTokenExpiry = accessTokenExpiry;
        _refreshTokenExpiry = refreshTokenExpiry;
    }

    /// <summary>
    /// An issuer for these claims. Returns a copy: the registered issuer is a shared singleton, and
    /// setting the claims on it let concurrent requests issue tokens with each other's claims.
    /// </summary>
    public JwtTokenIssuer WithClaims(IEnumerable<Claim> claims)
    {
        return new JwtTokenIssuer(JwtOptions, _accessTokenExpiry, _refreshTokenExpiry)
        {
            _customClaims = claims?.ToList(),
        };
    }

    /// <summary>
    /// Sets the refresh JWT lifetime (longer than the access lifetime) and returns this issuer. Mutates the instance: call it on a copy from
    /// <see cref="WithClaims"/>, not on the shared singleton.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="expiration"/> is not longer than the access lifetime.</exception>
    public JwtTokenIssuer SetRefreshTokenExpiry(TimeSpan expiration)
    {
        if (expiration <= _accessTokenExpiry)
            throw new ArgumentOutOfRangeException(nameof(expiration), expiration, "The refresh lifetime must be longer than the access lifetime.");
        _refreshTokenExpiry = expiration;
        return this;
    }

    private string GenerateJwtToken(IEnumerable<Claim> claims, DateTime expires)
    {
        var signingKey = JwtOptions.TokenValidationParameters.IssuerSigningKey
            as SymmetricSecurityKey ?? throw new InvalidOperationException("Signing key is not configured.");

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: JwtOptions.TokenValidationParameters.ValidIssuer,
            audience: JwtOptions.TokenValidationParameters.ValidAudience,
            claims: claims,
            expires: expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Issues a <see cref="JwtToken"/>: access JWT valid for the access lifetime, refresh JWT for the refresh lifetime, both with the
    /// claims from <see cref="WithClaims"/> and a new <c>jti</c>. <see cref="TokenIssue.PrincipalId"/> is the <c>sub</c> claim.
    /// </summary>
    /// <exception cref="InvalidOperationException">When no symmetric signing key is configured.</exception>
    public IIssue Issue()
    {
        var signingKey = JwtOptions.TokenValidationParameters.IssuerSigningKey
            as SymmetricSecurityKey
            ?? throw new InvalidOperationException("Signing key is not configured.");

        var creds = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

        if (_customClaims != null)
            claims.AddRange(_customClaims);

        var subject =
            claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;

        // JWT exp has whole-second resolution; the reported expirations must equal it.
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var accessExpiration = now + _accessTokenExpiry;
        var refreshExpiration = now + _refreshTokenExpiry;
        var accessToken = GenerateJwtToken(claims, accessExpiration);
        var refreshToken = GenerateJwtToken(claims, refreshExpiration) + ";jwt";

        return new JwtToken
        {
            AccessToken = $"{accessToken};jwt",
            RefreshToken = refreshToken,
            AccessExpiration = accessExpiration,
            RefreshExpiration = refreshExpiration,
            Algorithm = creds.Algorithm,
            PrincipalId = subject ?? ""
        };
    }

}



/// <summary>Ad-hoc issuer factories for code without DI. Prefer injecting <see cref="ISessionTokenIssuer"/> / <see cref="IJwtTokenIssuer"/>.</summary>
public static class Issuer
{
    /// <summary>A shared <see cref="SessionTokenIssuer"/> with default lifetimes (1 hour / 7 days).</summary>
    public static IIssuer Session = new SessionTokenIssuer();
    /// <summary>A new <see cref="JwtTokenIssuer"/> over <paramref name="jwtOptions"/>.</summary>
    public static IIssuer Jwt(IOptionsMonitor<JwtBearerOptions> jwtOptions) => new JwtTokenIssuer(jwtOptions);
}
