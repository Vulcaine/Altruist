/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist.Dashboard;

/// <summary>
/// How the Altruist dashboard (UI at <c>/altruist/dashboard</c>, JSON APIs under <c>/dashboard/v1</c>, live socket at
/// <c>/ws/dashboard</c>) is protected, read from <c>altruist:dashboard:*</c>. Enforced by
/// <see cref="DashboardAccessMiddleware"/>, which the framework installs whenever the dashboard is enabled.
/// </summary>
/// <remarks>
/// <para>Access is granted by <b>either</b> of:</para>
/// <list type="bullet">
/// <item><description><b>Token</b> — <c>altruist:dashboard:token</c>, a shared secret of at least
/// <see cref="MinimumTokenLength"/> characters. Clients send it as the <see cref="TokenHeader"/> header or as
/// <c>Authorization: Bearer &lt;token&gt;</c>. A browser opens <c>/altruist/dashboard?dashboard_token=&lt;token&gt;</c>
/// once: the token is exchanged for an HttpOnly, SameSite=Strict session cookie (<see cref="CookieName"/>, derived
/// from the token, so rotating the token logs everyone out) and the URL is reloaded without it. Compared in constant time.
/// Best for tools, scripts and small teams.</description></item>
/// <item><description><b>Policy</b> — <c>altruist:dashboard:policy</c>, the name of an ASP.NET Core authorization
/// policy you registered (<c>services.AddAuthorization(o =&gt; o.AddPolicy("ops", ...))</c>). The request's user (the
/// default authentication scheme, e.g. your JWT/cookie login) must satisfy it. Best when operators already sign in.</description></item>
/// </list>
/// <para>With neither configured the dashboard is <b>closed</b>: in the <c>Development</c> environment it answers
/// loopback requests only (no proxy headers; turn off with <c>altruist:dashboard:allow-loopback-without-token: false</c>),
/// in any other environment every dashboard request gets <c>503</c> and startup logs a critical error. The game server
/// itself keeps running — a monitoring tool should never take a production server down, and nothing is exposed.</para>
/// </remarks>
/// <example>
/// <code>
/// altruist:
///   dashboard:
///     enabled: true
///     token: ${DASHBOARD_TOKEN}   # >= 16 chars; or:
///     # policy: ops
/// </code>
/// </example>
public sealed class DashboardAccessOptions
{
    /// <summary><c>altruist:dashboard:enabled</c> — maps the dashboard controllers, UI and socket when <c>true</c>.</summary>
    public const string EnabledKey = "altruist:dashboard:enabled";
    /// <summary><c>altruist:dashboard:token</c> — shared secret that grants access (see the class remarks).</summary>
    public const string TokenKey = "altruist:dashboard:token";
    /// <summary><c>altruist:dashboard:policy</c> — ASP.NET Core authorization policy that grants access.</summary>
    public const string PolicyKey = "altruist:dashboard:policy";
    /// <summary><c>altruist:dashboard:allow-loopback-without-token</c> — Development-only loopback access without credentials (default <c>true</c>).</summary>
    public const string AllowLoopbackKey = "altruist:dashboard:allow-loopback-without-token";

    /// <summary>Request header carrying the token.</summary>
    public const string TokenHeader = "X-Altruist-Dashboard-Token";
    /// <summary>Session cookie set after a browser logs in with <see cref="LoginQueryParameter"/>.</summary>
    public const string CookieName = "altruist_dashboard";
    /// <summary>Query parameter a browser uses once to exchange the token for the session cookie.</summary>
    public const string LoginQueryParameter = "dashboard_token";
    /// <summary>Shortest accepted token; a shorter one counts as misconfigured (dashboard closed with 503).</summary>
    public const int MinimumTokenLength = 16;

    /// <summary>Whether the dashboard is enabled.</summary>
    public bool Enabled { get; init; }
    /// <summary>The configured token (null when unset; may be too short — see <see cref="TokenUsable"/>).</summary>
    public string? Token { get; init; }
    /// <summary>The configured authorization policy name, or null.</summary>
    public string? Policy { get; init; }
    /// <summary>Whether Development loopback access without credentials is allowed (default <c>true</c>).</summary>
    public bool AllowLoopbackInDevelopment { get; init; } = true;

    /// <summary>True when a token of at least <see cref="MinimumTokenLength"/> characters is configured.</summary>
    public bool TokenUsable => Token is { Length: >= MinimumTokenLength };

    /// <summary>True when a token was configured but is shorter than <see cref="MinimumTokenLength"/>.</summary>
    public bool TokenTooShort => !string.IsNullOrEmpty(Token) && !TokenUsable;

    /// <summary>True when a usable token or a policy grants access.</summary>
    public bool HasCredentials => TokenUsable || !string.IsNullOrWhiteSpace(Policy);

    /// <summary>Reads the options from configuration. Cheap; the middleware calls it per request so live edits apply.</summary>
    /// <param name="configuration">App configuration.</param>
    /// <returns>The options.</returns>
    public static DashboardAccessOptions FromConfiguration(IConfiguration configuration)
    {
        static bool Flag(string? v, bool fallback) =>
            string.IsNullOrWhiteSpace(v) ? fallback : string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        var token = configuration[TokenKey];
        var policy = configuration[PolicyKey];
        return new DashboardAccessOptions
        {
            Enabled = Flag(configuration[EnabledKey], false),
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            Policy = string.IsNullOrWhiteSpace(policy) ? null : policy.Trim(),
            AllowLoopbackInDevelopment = Flag(configuration[AllowLoopbackKey], true),
        };
    }
}

/// <summary>
/// Helpers shared by the dashboard protection layer and the dashboard controllers: which paths are dashboard paths,
/// the startup status log, and secret redaction for configuration listings.
/// </summary>
public static class DashboardAccess
{
    /// <summary>Path prefixes guarded by <see cref="DashboardAccessMiddleware"/> (matched per path segment, case-insensitive).</summary>
    public static readonly IReadOnlyList<string> ProtectedPathPrefixes = new[] { "/dashboard/v1", "/altruist/dashboard", "/ws/dashboard" };

    /// <summary>Placeholder returned instead of a secret configuration value.</summary>
    public const string RedactedValue = "***";

    private static readonly Regex SecretKeyPattern = new(
        @"(password|passwd|pwd|secret|token|apikey|api-key|api_key|(^|[:_\-.])key($|[:_\-.])|privatekey|private-key|private_key|connection-?string|connectionstring|connstr|credential)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Whether <paramref name="path"/> is a dashboard path (UI, API or socket).</summary>
    /// <param name="path">Request path (after any path base).</param>
    /// <returns>True for a guarded path.</returns>
    public static bool IsDashboardPath(PathString path)
    {
        foreach (var prefix in ProtectedPathPrefixes)
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Whether a configuration key names a secret: any segment containing <c>password</c>, <c>secret</c>,
    /// <c>token</c>, <c>key</c> (as a whole word, e.g. <c>signing-key</c>, <c>api-key</c>), <c>credential</c> or
    /// <c>connection-string</c>. Use it before showing configuration to anyone.
    /// </summary>
    /// <param name="key">Colon-separated configuration key.</param>
    /// <returns>True when the value must be redacted.</returns>
    public static bool IsSecretKey(string? key) => !string.IsNullOrEmpty(key) && SecretKeyPattern.IsMatch(key);

    /// <summary>
    /// Returns <see cref="RedactedValue"/> for a non-empty value under a secret key (<see cref="IsSecretKey"/>) or a
    /// value that looks like a connection string with a password; otherwise the value unchanged.
    /// </summary>
    /// <param name="key">Configuration key.</param>
    /// <param name="value">Configuration value.</param>
    /// <returns>The value to display.</returns>
    public static string? RedactValue(string? key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (IsSecretKey(key))
            return RedactedValue;
        if (value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("pwd=", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(value, @"^[a-z][a-z0-9+.\-]*://[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase))
            return RedactedValue;
        return value;
    }

    /// <summary>
    /// Logs how the dashboard is protected: information when credentials are configured, a warning for
    /// Development loopback-only access, a critical error (plus a console line) when the dashboard is closed.
    /// Called once at startup by the framework.
    /// </summary>
    /// <param name="options">Current options.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="logger">Destination logger.</param>
    public static void LogStartupStatus(DashboardAccessOptions options, IHostEnvironment environment, ILogger logger)
    {
        if (!options.Enabled)
            return;

        if (options.TokenTooShort)
            logger.LogError("Dashboard token ({Key}) is shorter than {Min} characters and is ignored.",
                DashboardAccessOptions.TokenKey, DashboardAccessOptions.MinimumTokenLength);

        if (options.HasCredentials)
        {
            logger.LogInformation("Dashboard enabled and protected by {Mode}.",
                options.TokenUsable && options.Policy is not null ? "token or policy '" + options.Policy + "'"
                : options.TokenUsable ? "token" : "policy '" + options.Policy + "'");
            return;
        }

        if (environment.IsDevelopment() && options.AllowLoopbackInDevelopment)
        {
            logger.LogWarning(
                "Dashboard enabled without {TokenKey} or {PolicyKey}: Development mode, loopback requests only.",
                DashboardAccessOptions.TokenKey, DashboardAccessOptions.PolicyKey);
            return;
        }

        var msg =
            $"DASHBOARD CLOSED: {DashboardAccessOptions.EnabledKey} is true but neither {DashboardAccessOptions.TokenKey} " +
            $"(>= {DashboardAccessOptions.MinimumTokenLength} chars) nor {DashboardAccessOptions.PolicyKey} is configured " +
            $"(environment '{environment.EnvironmentName}'). Every dashboard request answers 503 until one is set.";
        logger.LogCritical("{Message}", msg);
        Console.WriteLine("🔒 " + msg);
    }

    /// <summary>The session cookie value derived from <paramref name="token"/> (HMAC-SHA256, base64url). Changes when the token changes.</summary>
    /// <param name="token">The configured token.</param>
    /// <returns>Cookie value.</returns>
    public static string SessionCookieValue(string token)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes("altruist-dashboard-session-v1"));
        return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Constant-time comparison of two secrets (hashes both first so lengths do not leak).</summary>
    /// <param name="presented">Value sent by the client.</param>
    /// <param name="expected">Expected secret.</param>
    /// <returns>True when equal.</returns>
    public static bool SecretEquals(string? presented, string expected)
    {
        if (presented is null)
            return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Whether the request comes straight from this machine: loopback remote address and no proxy headers
    /// (<c>X-Forwarded-For</c>, <c>Forwarded</c>, <c>X-Real-IP</c>), so a local reverse proxy does not make remote
    /// clients look local.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>True for a direct loopback request.</returns>
    public static bool IsDirectLoopback(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP"))
            return false;
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }
}

/// <summary>
/// Mandatory protection for every dashboard path (<see cref="DashboardAccess.ProtectedPathPrefixes"/>): lets a
/// request through only with a valid token, a satisfied authorization policy, or — in Development without
/// credentials configured — from loopback. See <see cref="DashboardAccessOptions"/> for the rules and config keys.
/// </summary>
/// <remarks>
/// Installed by the framework when <c>altruist:dashboard:enabled</c> is <c>true</c> (after authentication, before
/// endpoints) and again by the dashboard UI's startup filter ahead of its static files; a request checked once is not
/// checked again. Non-dashboard paths pass through untouched. Responses: <c>401</c> (no credentials),
/// <c>403</c> (wrong credentials, or non-loopback in Development), <c>503</c> (dashboard closed: no credentials
/// configured outside Development). You do not add it yourself.
/// </remarks>
public sealed class DashboardAccessMiddleware
{
    private const string CheckedItem = "altruist.dashboard.access";

    private readonly RequestDelegate _next;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger _logger;

    /// <summary>Created by <c>UseMiddleware</c>.</summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="configuration">App configuration (read per request).</param>
    /// <param name="environment">Host environment (Development enables loopback access).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public DashboardAccessMiddleware(RequestDelegate next, IConfiguration configuration, IHostEnvironment environment, ILoggerFactory loggerFactory)
    {
        _next = next;
        _configuration = configuration;
        _environment = environment;
        _logger = loggerFactory.CreateLogger<DashboardAccessMiddleware>();
    }

    /// <summary>Checks the request and either continues the pipeline or writes the rejection.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Items.ContainsKey(CheckedItem) || !DashboardAccess.IsDashboardPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var options = DashboardAccessOptions.FromConfiguration(_configuration);
        if (!options.Enabled)
        {
            await Reject(context, StatusCodes.Status404NotFound, "dashboard_disabled", "The dashboard is disabled.");
            return;
        }

        if (options.HasCredentials)
        {
            var presentedAny = false;

            if (options.TokenUsable)
            {
                var token = options.Token!;
                var header = context.Request.Headers[DashboardAccessOptions.TokenHeader].ToString();
                var auth = context.Request.Headers.Authorization.ToString();
                var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
                var cookie = context.Request.Cookies[DashboardAccessOptions.CookieName];
                var query = context.Request.Query[DashboardAccessOptions.LoginQueryParameter].ToString();

                presentedAny = header.Length > 0 || bearer is not null || cookie is not null || query.Length > 0;

                if ((header.Length > 0 && DashboardAccess.SecretEquals(header, token)) ||
                    (bearer is not null && DashboardAccess.SecretEquals(bearer, token)) ||
                    (cookie is not null && DashboardAccess.SecretEquals(cookie, DashboardAccess.SessionCookieValue(token))))
                {
                    await Allow(context);
                    return;
                }

                if (query.Length > 0 && DashboardAccess.SecretEquals(query, token))
                {
                    context.Response.Cookies.Append(DashboardAccessOptions.CookieName, DashboardAccess.SessionCookieValue(token), new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = context.Request.IsHttps,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        IsEssential = true,
                    });

                    // Browser login: reload without the token so it does not stay in history or referrers.
                    if (HttpMethods.IsGet(context.Request.Method) && !context.WebSockets.IsWebSocketRequest)
                    {
                        var rest = context.Request.Query
                            .Where(kv => !string.Equals(kv.Key, DashboardAccessOptions.LoginQueryParameter, StringComparison.OrdinalIgnoreCase))
                            .SelectMany(kv => kv.Value.Select(v => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(v ?? "")));
                        var qs = string.Join("&", rest);
                        context.Response.Redirect(context.Request.PathBase + context.Request.Path + (qs.Length > 0 ? "?" + qs : ""));
                        return;
                    }

                    await Allow(context);
                    return;
                }
            }

            if (options.Policy is { } policy)
            {
                var user = context.User;
                if (user.Identity?.IsAuthenticated != true)
                {
                    try
                    {
                        var result = await context.AuthenticateAsync();
                        if (result.Succeeded && result.Principal is not null)
                            context.User = user = result.Principal;
                    }
                    catch (InvalidOperationException)
                    {
                        // No authentication scheme registered: stays anonymous.
                    }
                }

                var authz = context.RequestServices.GetService<IAuthorizationService>();
                if (authz is null)
                {
                    _logger.LogError("Dashboard policy '{Policy}' is configured but authorization services are not registered.", policy);
                    await Reject(context, StatusCodes.Status503ServiceUnavailable, "dashboard_closed", "Dashboard authorization is misconfigured.");
                    return;
                }

                AuthorizationResult decision;
                try
                {
                    decision = await authz.AuthorizeAsync(user, null, policy);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Dashboard policy '{Policy}' could not be evaluated.", policy);
                    await Reject(context, StatusCodes.Status503ServiceUnavailable, "dashboard_closed", "Dashboard authorization is misconfigured.");
                    return;
                }

                if (decision.Succeeded)
                {
                    await Allow(context);
                    return;
                }

                presentedAny |= user.Identity?.IsAuthenticated == true;
            }

            if (presentedAny)
            {
                await Reject(context, StatusCodes.Status403Forbidden, "dashboard_forbidden", "Invalid dashboard credentials.");
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"altruist-dashboard\"";
            await Reject(context, StatusCodes.Status401Unauthorized, "dashboard_unauthorized",
                $"Dashboard credentials required (header {DashboardAccessOptions.TokenHeader}).");
            return;
        }

        if (_environment.IsDevelopment() && options.AllowLoopbackInDevelopment)
        {
            if (DashboardAccess.IsDirectLoopback(context))
            {
                await Allow(context);
                return;
            }

            await Reject(context, StatusCodes.Status403Forbidden, "dashboard_loopback_only",
                $"Without {DashboardAccessOptions.TokenKey} the dashboard only answers loopback requests.");
            return;
        }

        await Reject(context, StatusCodes.Status503ServiceUnavailable, "dashboard_closed",
            $"Dashboard is closed: configure {DashboardAccessOptions.TokenKey} or {DashboardAccessOptions.PolicyKey}.");
    }

    private Task Allow(HttpContext context)
    {
        context.Items[CheckedItem] = true;
        return _next(context);
    }

    private static async Task Reject(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(
            "{\"error\":\"" + code + "\",\"message\":\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
    }
}
