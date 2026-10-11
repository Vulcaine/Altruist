/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Security;

/// <summary>
/// The refresh token as an HttpOnly cookie, out of reach of page scripts. Config
/// <c>altruist:security:refresh-cookie</c>: <c>name</c> ("refresh_token"), <c>path</c> (the auth
/// routes that read it, "/"), <c>same-site</c> (strict | lax | none; "strict"), <c>csrf-header</c>
/// ("X-Requested-With") and <c>csrf-value</c> ("1"). The cookie is <c>Secure</c> on HTTPS requests
/// (<see cref="HttpRequest.IsHttps"/>, which honours forwarded headers).
/// <para>
/// Endpoints authenticated by the cookie must check <see cref="HasCsrfHeader"/>: cross-site forms
/// cannot send a custom header, and cross-origin scripts would need a CORS preflight.
/// </para>
/// </summary>
[Service]
public sealed class RefreshCookie
{
    /// <summary>Config section of the cookie settings.</summary>
    public const string ConfigPath = "altruist:security:refresh-cookie";

    /// <summary>Cookie name (<c>name</c>, default <c>refresh_token</c>).</summary>
    public string Name { get; init; } = "refresh_token";
    /// <summary>Cookie path (<c>path</c>, default <c>/</c>); narrow it to your auth routes so the cookie is not sent elsewhere.</summary>
    public string Path { get; init; } = "/";
    /// <summary>SameSite mode (<c>same-site</c>, default Strict). <c>None</c> forces <c>Secure</c>.</summary>
    public SameSiteMode SameSite { get; init; } = SameSiteMode.Strict;
    /// <summary>Header that cookie-authenticated requests must carry (<c>csrf-header</c>, default <c>X-Requested-With</c>).</summary>
    public string CsrfHeader { get; init; } = "X-Requested-With";
    /// <summary>Required value of <see cref="CsrfHeader"/> (<c>csrf-value</c>, default <c>1</c>).</summary>
    public string CsrfValue { get; init; } = "1";

    /// <summary>DI constructor: reads the application configuration (registered as a singleton by <c>[Service]</c>).</summary>
    [ActivatorUtilitiesConstructor]
    public RefreshCookie() : this(AppConfigLoader.Load()) { }

    /// <summary>Reads the settings from <paramref name="configuration"/> (section <see cref="ConfigPath"/>).</summary>
    /// <exception cref="ArgumentException">When <c>same-site</c> is not strict, lax or none.</exception>
    public RefreshCookie(IConfiguration configuration)
    {
        var section = (configuration ?? throw new ArgumentNullException(nameof(configuration))).GetSection(ConfigPath);
        Name = TokenConfig.Text(section, "name") ?? Name;
        Path = TokenConfig.Text(section, "path") ?? Path;
        CsrfHeader = TokenConfig.Text(section, "csrf-header") ?? CsrfHeader;
        CsrfValue = TokenConfig.Text(section, "csrf-value") ?? CsrfValue;
        SameSite = TokenConfig.Text(section, "same-site")?.ToLowerInvariant() switch
        {
            null or "strict" => SameSiteMode.Strict,
            "lax" => SameSiteMode.Lax,
            "none" => SameSiteMode.None,
            var other => throw new ArgumentException($"{ConfigPath}:same-site must be strict, lax or none, not '{other}'."),
        };
    }

    /// <summary>The raw token the client sent, or null.</summary>
    public string? Read(HttpRequest request) => request.Cookies[Name];

    /// <summary>Sets the cookie to <paramref name="token"/>, expiring at <paramref name="expires"/>.</summary>
    /// <param name="response">The response to set the cookie on.</param>
    /// <param name="token">The raw refresh token.</param>
    /// <param name="expires">Cookie expiry.</param>
    public void Append(HttpResponse response, string token, DateTimeOffset expires) =>
        response.Cookies.Append(Name, token, Options(response.HttpContext.Request, expires));

    /// <summary>Sets the cookie to a token from <see cref="IRefreshTokenService"/>, expiring with it. Call after every issue and rotation.</summary>
    /// <param name="response">The response to set the cookie on.</param>
    /// <param name="token">The issued token.</param>
    public void Append(HttpResponse response, IssuedRefreshToken token) =>
        Append(response, token.Token, new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc)));

    /// <summary>Removes the cookie (sign-out, or after a failed rotation).</summary>
    public void Delete(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(response.HttpContext.Request, null));

    /// <summary>True when the request carries <see cref="CsrfHeader"/> with <see cref="CsrfValue"/>.</summary>
    public bool HasCsrfHeader(HttpRequest request) => request.Headers[CsrfHeader] == CsrfValue;

    /// <summary>
    /// The cookie options used by <see cref="Append(HttpResponse, string, DateTimeOffset)"/> and <see cref="Delete"/>:
    /// HttpOnly, essential, <see cref="SameSite"/>, <see cref="Path"/>, and <c>Secure</c> on HTTPS requests or SameSite None.
    /// </summary>
    /// <param name="request">The current request (decides <c>Secure</c>).</param>
    /// <param name="expires">Cookie expiry, or null for a session cookie.</param>
    public CookieOptions Options(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSite,
        Secure = request.IsHttps || SameSite == SameSiteMode.None,
        Path = Path,
        Expires = expires,
        IsEssential = true,
    };
}
