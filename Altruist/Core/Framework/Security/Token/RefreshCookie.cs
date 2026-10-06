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
    public const string ConfigPath = "altruist:security:refresh-cookie";

    public string Name { get; init; } = "refresh_token";
    public string Path { get; init; } = "/";
    public SameSiteMode SameSite { get; init; } = SameSiteMode.Strict;
    public string CsrfHeader { get; init; } = "X-Requested-With";
    public string CsrfValue { get; init; } = "1";

    [ActivatorUtilitiesConstructor]
    public RefreshCookie() : this(AppConfigLoader.Load()) { }

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

    public void Append(HttpResponse response, string token, DateTimeOffset expires) =>
        response.Cookies.Append(Name, token, Options(response.HttpContext.Request, expires));

    public void Append(HttpResponse response, IssuedRefreshToken token) =>
        Append(response, token.Token, new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc)));

    public void Delete(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(response.HttpContext.Request, null));

    /// <summary>True when the request carries <see cref="CsrfHeader"/> with <see cref="CsrfValue"/>.</summary>
    public bool HasCsrfHeader(HttpRequest request) => request.Headers[CsrfHeader] == CsrfValue;

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
