/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

/// <summary>A signed access token and how long it is valid.</summary>
/// <param name="Token">The compact JWT (no protocol suffix); send it as <c>Authorization: Bearer</c>.</param>
/// <param name="ExpiresInSeconds">Lifetime in whole seconds (for an OAuth-style <c>expires_in</c>).</param>
/// <param name="ExpiresAt">UTC expiry instant.</param>
public sealed record IssuedAccessToken(string Token, int ExpiresInSeconds, DateTime ExpiresAt);

/// <summary>
/// Issues short-lived JWT access tokens that the JWT bearer setup of <see cref="AuthConfiguration"/>
/// validates (same key, issuer and audience). Stateless and safe to share: every call gets its own
/// claims. Pair it with <see cref="IRefreshTokenService"/> for sessions that outlive the token.
/// Registered (singleton) only with <c>altruist:security:mode: jwt</c> (the default mode).
/// </summary>
/// <remarks>
/// Use this for new HTTP sign-in flows instead of the legacy <see cref="IJwtTokenIssuer"/> (fixed 1-hour token,
/// stateful refresh). Clients send the token as <c>Authorization: Bearer ...</c>; ASP.NET's JWT bearer
/// authentication (<c>[Authorize]</c>) and <see cref="JwtShieldAttribute"/> accept it. For WebSockets, trade it for
/// a ticket from <see cref="IConnectionTicketService"/>.
/// </remarks>
/// <example>
/// <code>
/// var access = accessTokens.Issue(account.StorageId, new[] { new Claim("role", "player") });
/// var refresh = await refreshTokens.IssueAsync(account.StorageId);
/// refreshCookie.Append(Response, refresh);
/// return Ok(new { accessToken = access.Token, expiresIn = access.ExpiresInSeconds });
/// </code>
/// </example>
public interface IAccessTokenIssuer
{
    /// <summary>Default lifetime (<c>altruist:security:access-token-minutes</c>).</summary>
    TimeSpan Lifetime { get; }

    /// <summary>
    /// A HS256 token with <c>sub</c> = <paramref name="subject"/>, the <paramref name="claims"/> and a
    /// random <c>jti</c>, valid for <paramref name="lifetime"/> (default <see cref="Lifetime"/>).
    /// </summary>
    IssuedAccessToken Issue(string subject, IEnumerable<Claim>? claims = null, TimeSpan? lifetime = null);
}

/// <summary>
/// The default <see cref="IAccessTokenIssuer"/>: signs with the <see cref="TokenValidationParameters"/>
/// registered by <see cref="AuthConfiguration"/> (<c>altruist:security:mode: jwt</c>). Config
/// <c>altruist:security:access-token-minutes</c> (default 15).
/// </summary>
[Service(typeof(IAccessTokenIssuer), DependsOn = new[] { typeof(AuthConfiguration) })]
[ConditionalOnConfig("altruist:security")]
[ConditionalOnConfig("altruist:security:mode", havingValue: "jwt")]
public sealed class AccessTokenIssuer : IAccessTokenIssuer
{
    /// <summary>Config key of the default lifetime, in minutes (default 15).</summary>
    public const string LifetimeKey = "altruist:security:access-token-minutes";

    private readonly TokenValidationParameters _validation;
    private readonly Func<DateTime> _utcNow;
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>DI constructor: lifetime from <see cref="LifetimeKey"/> (minutes, default 15).</summary>
    /// <param name="validation">The validation parameters registered by <see cref="AuthConfiguration"/> (key, issuer, audience).</param>
    /// <param name="lifetimeMinutes">Default token lifetime in minutes.</param>
    [ActivatorUtilitiesConstructor]
    public AccessTokenIssuer(
        TokenValidationParameters validation,
        [AppConfigValue(LifetimeKey, "15")] double lifetimeMinutes)
        : this(validation, TimeSpan.FromMinutes(lifetimeMinutes), null) { }

    /// <summary>An issuer with an explicit lifetime and clock (tests).</summary>
    /// <param name="validation">Supplies the signing key, issuer and audience.</param>
    /// <param name="lifetime">Default token lifetime; must be positive.</param>
    /// <param name="utcNow">UTC clock; null for <see cref="DateTime.UtcNow"/>.</param>
    public AccessTokenIssuer(TokenValidationParameters validation, TimeSpan lifetime, Func<DateTime>? utcNow)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, $"{LifetimeKey} must be positive.");
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Lifetime = lifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc/>
    public TimeSpan Lifetime { get; }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">When <paramref name="subject"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">When no signing key is configured.</exception>
    public IssuedAccessToken Issue(string subject, IEnumerable<Claim>? claims = null, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrEmpty(subject))
            throw new ArgumentException("An access token needs a subject.", nameof(subject));
        var key = _validation.IssuerSigningKey ?? throw new InvalidOperationException("JWT signing key is not configured.");
        var now = _utcNow();
        var span = lifetime ?? Lifetime;
        var expires = now + span;

        var all = new List<Claim> { new(JwtRegisteredClaimNames.Sub, subject) };
        if (claims is not null)
            all.AddRange(claims.Where(c => c.Type is not (JwtRegisteredClaimNames.Sub or JwtRegisteredClaimNames.Jti)));
        all.Add(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _validation.ValidIssuer,
            Audience = _validation.ValidAudience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(all),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        return new IssuedAccessToken(_handler.WriteToken(_handler.CreateToken(descriptor)), (int)span.TotalSeconds, expires);
    }
}
