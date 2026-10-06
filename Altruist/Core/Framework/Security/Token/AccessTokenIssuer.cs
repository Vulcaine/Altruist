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
public sealed record IssuedAccessToken(string Token, int ExpiresInSeconds, DateTime ExpiresAt);

/// <summary>
/// Issues short-lived JWT access tokens that the JWT bearer setup of <see cref="AuthConfiguration"/>
/// validates (same key, issuer and audience). Stateless and safe to share: every call gets its own
/// claims. Pair it with <see cref="IRefreshTokenService"/> for sessions that outlive the token.
/// </summary>
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
    public const string LifetimeKey = "altruist:security:access-token-minutes";

    private readonly TokenValidationParameters _validation;
    private readonly Func<DateTime> _utcNow;
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    [ActivatorUtilitiesConstructor]
    public AccessTokenIssuer(
        TokenValidationParameters validation,
        [AppConfigValue(LifetimeKey, "15")] double lifetimeMinutes)
        : this(validation, TimeSpan.FromMinutes(lifetimeMinutes), null) { }

    /// <summary>An issuer with an explicit lifetime and clock (tests).</summary>
    public AccessTokenIssuer(TokenValidationParameters validation, TimeSpan lifetime, Func<DateTime>? utcNow)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, $"{LifetimeKey} must be positive.");
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Lifetime = lifetime;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public TimeSpan Lifetime { get; }

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
