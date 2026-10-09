/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;
using System.Net;

using Altruist.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Http;

/// <summary>
/// <c>altruist:server:http:hardening</c>: HTTP hardening, every part off unless configured.
/// <list type="bullet">
/// <item><c>server-header</c> (true): false drops Kestrel's <c>Server</c> header.</item>
/// <item><c>max-body-bytes</c>: Kestrel's request body limit for every request (Kestrel's default: 30 MB).</item>
/// <item><c>forwarded-headers</c>: <c>enabled</c>, <c>forward-limit</c> (1), <c>known-proxies</c> (addresses; default
/// loopback only): <c>X-Forwarded-For</c> / <c>-Proto</c> from trusted proxies set the client address and scheme.</item>
/// <item><c>security-headers</c>: <c>enabled</c> (nosniff, <c>Referrer-Policy: no-referrer</c>, <c>X-Frame-Options: DENY</c>,
/// <c>Cross-Origin-Resource-Policy: same-origin</c>) and <c>hsts-max-age-seconds</c> (HSTS on HTTPS requests; 0 = none).</item>
/// <item><c>api</c>: for requests under <c>path</c>, a <c>content-security-policy</c>, <c>no-store</c> (<c>Cache-Control</c>) and
/// <c>max-body-bytes</c> (larger bodies are answered 413 <c>payload_too_large</c>).</item>
/// </list>
/// The middleware runs first in the pipeline (before the fleet relay, routing and authentication),
/// in this order: forwarded headers, response headers, path rate limits (<see cref="HttpRateLimitOptions"/>), API body limit.
/// </summary>
/// <remarks>
/// Read once at startup by <see cref="HttpApiConfiguration"/>; configure it in YAML rather than constructing it. Enable
/// <c>forwarded-headers</c> only behind a reverse proxy you control, and list that proxy in <c>known-proxies</c>
/// unless it runs on loopback; otherwise client addresses (used by rate limits) are spoofable or wrong.
/// </remarks>
/// <example>
/// <code>
/// altruist:
///   server:
///     http:
///       hardening:
///         server-header: false
///         max-body-bytes: 1048576
///         forwarded-headers: { enabled: true, forward-limit: 1, known-proxies: [ "10.0.0.5" ] }
///         security-headers: { enabled: true, hsts-max-age-seconds: 31536000 }
///         api: { path: /api, content-security-policy: "default-src 'none'", no-store: true, max-body-bytes: 65536 }
/// </code>
/// </example>
public sealed class HttpHardeningOptions
{
    /// <summary>Config section: <c>altruist:server:http:hardening</c>.</summary>
    public const string ConfigPath = "altruist:server:http:hardening";

    /// <summary><c>server-header</c>: keep Kestrel's <c>Server</c> response header (default <c>true</c>).</summary>
    public bool ServerHeader { get; set; } = true;
    /// <summary><c>max-body-bytes</c>: Kestrel request body limit in bytes for all requests; <c>null</c> keeps Kestrel's default (30 MB).</summary>
    public long? MaxBodyBytes { get; set; }

    /// <summary><c>forwarded-headers:enabled</c>: honour <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> from trusted proxies.</summary>
    public bool ForwardedHeaders { get; set; }
    /// <summary><c>forwarded-headers:forward-limit</c>: number of proxy hops to unwind (default 1).</summary>
    public int ForwardLimit { get; set; } = 1;
    /// <summary><c>forwarded-headers:known-proxies</c>: trusted proxy addresses, added to ASP.NET Core's loopback defaults.</summary>
    public List<IPAddress> KnownProxies { get; set; } = new();

    /// <summary><c>security-headers:enabled</c>: add nosniff, <c>Referrer-Policy</c>, <c>X-Frame-Options</c> and <c>Cross-Origin-Resource-Policy</c> to every response.</summary>
    public bool SecurityHeaders { get; set; }
    /// <summary><c>security-headers:hsts-max-age-seconds</c>: HSTS max-age in seconds on HTTPS requests (0 = no HSTS). Only applied when <see cref="SecurityHeaders"/> is on.</summary>
    public int HstsMaxAgeSeconds { get; set; }

    /// <summary><c>api:path</c>: path prefix (segment match) the <c>Api*</c> settings apply to; without it they are ignored.</summary>
    public string? ApiPath { get; set; }
    /// <summary><c>api:content-security-policy</c>: <c>Content-Security-Policy</c> header value for API responses.</summary>
    public string? ApiContentSecurityPolicy { get; set; }
    /// <summary><c>api:no-store</c>: send <c>Cache-Control: no-store</c> on API responses.</summary>
    public bool ApiNoStore { get; set; }
    /// <summary><c>api:max-body-bytes</c>: body limit in bytes for API requests; larger declared bodies get 413 <c>payload_too_large</c>, chunked ones are cut off by Kestrel.</summary>
    public long? ApiMaxBodyBytes { get; set; }

    internal bool HasApi => !string.IsNullOrEmpty(ApiPath) && (ApiContentSecurityPolicy is not null || ApiNoStore || ApiMaxBodyBytes is not null);

    /// <summary>Reads <c>altruist:server:http:hardening</c>; a missing section yields all-defaults (nothing enabled).</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException">A <c>known-proxies</c> entry is not an IP address.</exception>
    public static HttpHardeningOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new HttpHardeningOptions();
        if (!section.Exists())
            return options;

        options.ServerHeader = TokenConfig.Bool(section, "server-header") ?? options.ServerHeader;
        options.MaxBodyBytes = (long?)TokenConfig.Number(section, "max-body-bytes");

        var forwarded = section.GetSection("forwarded-headers");
        options.ForwardedHeaders = TokenConfig.Bool(forwarded, "enabled") ?? false;
        options.ForwardLimit = (int)(TokenConfig.Number(forwarded, "forward-limit") ?? options.ForwardLimit);
        options.KnownProxies = forwarded.GetSection("known-proxies").GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => IPAddress.TryParse(v!.Trim(), out var ip) ? ip : throw new ArgumentException($"{ConfigPath}:forwarded-headers:known-proxies: '{v}' is not an IP address."))
            .ToList();

        var headers = section.GetSection("security-headers");
        options.SecurityHeaders = TokenConfig.Bool(headers, "enabled") ?? false;
        options.HstsMaxAgeSeconds = (int)(TokenConfig.Number(headers, "hsts-max-age-seconds") ?? 0);

        var api = section.GetSection("api");
        options.ApiPath = TokenConfig.Text(api, "path");
        options.ApiContentSecurityPolicy = TokenConfig.Text(api, "content-security-policy");
        options.ApiNoStore = TokenConfig.Bool(api, "no-store") ?? false;
        options.ApiMaxBodyBytes = (long?)TokenConfig.Number(api, "max-body-bytes");
        return options;
    }

    /// <summary>Kestrel and forwarded-header options (only what is configured).</summary>
    internal void Register(IServiceCollection services)
    {
        if (!ServerHeader || MaxBodyBytes is not null)
        {
            var serverHeader = ServerHeader;
            var maxBody = MaxBodyBytes;
            services.Configure<KestrelServerOptions>(o =>
            {
                if (!serverHeader)
                    o.AddServerHeader = false;
                if (maxBody is { } max)
                    o.Limits.MaxRequestBodySize = max;
            });
        }

        if (ForwardedHeaders)
        {
            var limit = ForwardLimit;
            var proxies = KnownProxies.ToList();
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                // KnownNetworks / KnownProxies default to the loopback addresses.
                o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
                o.ForwardLimit = limit;
                foreach (var proxy in proxies)
                    o.KnownProxies.Add(proxy);
            });
        }
    }
}

/// <summary>Response headers, path rate limits and the API body limit (see <see cref="HttpHardeningOptions"/>).</summary>
/// <remarks>Added by <see cref="HttpHardeningExtensions.UseAltruistHttpHardening"/>; do not add it manually.
/// Path rate limits use the <see cref="IRequestRateLimiter"/> singleton and answer 429 <c>rate_limited</c> with <c>Retry-After</c>.</remarks>
public sealed class HttpHardeningMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HttpHardeningOptions _options;
    private readonly HttpRateLimitPolicy[] _pathPolicies;
    private readonly string? _hsts;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="settings">HTTP API settings (hardening and path rate-limit policies).</param>
    public HttpHardeningMiddleware(RequestDelegate next, HttpApiSettings settings)
    {
        _next = next;
        _options = settings.Hardening;
        _pathPolicies = settings.RateLimit.PathPolicies.ToArray();
        _hsts = _options.HstsMaxAgeSeconds > 0 ? "max-age=" + _options.HstsMaxAgeSeconds.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Applies headers, path rate limits and the API body limit, then calls the next middleware unless the request was rejected.</summary>
    /// <param name="context">The request context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        if (_options.SecurityHeaders)
        {
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (_hsts is not null && context.Request.IsHttps)
                headers.StrictTransportSecurity = _hsts;
        }

        var api = _options.HasApi && context.Request.Path.StartsWithSegments(_options.ApiPath);
        if (api)
        {
            if (_options.ApiContentSecurityPolicy is { } csp)
                headers.ContentSecurityPolicy = csp;
            if (_options.ApiNoStore)
                headers.CacheControl = "no-store";
        }

        if (_pathPolicies.Length > 0)
        {
            var limiter = context.RequestServices.GetRequiredService<IRequestRateLimiter>();
            foreach (var policy in _pathPolicies)
            {
                if (!context.Request.Path.StartsWithSegments(policy.Path))
                    continue;
                var decision = await limiter.HitAsync(policy.Name, context, context.RequestAborted).ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    HttpErrorWriter.SetRetryAfter(context.Response, decision.RetryAfterSeconds);
                    await context.RequestServices.GetRequiredService<HttpErrorWriter>()
                        .WriteAsync(context.Response, StatusCodes.Status429TooManyRequests, HttpErrorCodes.RateLimited).ConfigureAwait(false);
                    return;
                }
            }
        }

        if (api && _options.ApiMaxBodyBytes is { } limit)
        {
            if (context.Request.ContentLength > limit)
            {
                await context.RequestServices.GetRequiredService<HttpErrorWriter>()
                    .WriteAsync(context.Response, StatusCodes.Status413PayloadTooLarge, HttpErrorCodes.PayloadTooLarge).ConfigureAwait(false);
                return;
            }
            // Chunked bodies: Kestrel stops reading past the limit (BadHttpRequestException 413).
            var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
                bodySize.MaxRequestBodySize = limit;
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>Pipeline entry point for <see cref="HttpHardeningOptions"/>.</summary>
public static class HttpHardeningExtensions
{
    /// <summary>
    /// Adds the configured hardening (forwarded headers, security headers, path rate limits, API
    /// body limit) to the pipeline. Altruist's server calls it first; call it yourself only on a
    /// host you build (tests). Does nothing when none of it is configured.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="settings">Settings, normally the <see cref="HttpApiSettings"/> singleton from DI.</param>
    /// <returns><paramref name="app"/>.</returns>
    /// <example>
    /// <code>
    /// app.UseAltruistHttpHardening(app.ApplicationServices.GetRequiredService&lt;HttpApiSettings&gt;());
    /// </code>
    /// </example>
    public static IApplicationBuilder UseAltruistHttpHardening(this IApplicationBuilder app, HttpApiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Hardening.ForwardedHeaders)
            app.UseForwardedHeaders();
        var h = settings.Hardening;
        if (h.SecurityHeaders || h.HasApi || settings.RateLimit.PathPolicies.Any())
            app.UseMiddleware<HttpHardeningMiddleware>(settings);
        return app;
    }
}
