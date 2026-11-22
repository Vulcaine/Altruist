/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Security;

/// <summary>
/// <c>altruist:security:captcha</c>: <c>provider</c> (<c>turnstile</c>, the default, <c>hcaptcha</c> or
/// <c>recaptcha</c>: their siteverify endpoints take the same form), <c>site-key</c> (public, for
/// your client's widget), <c>secret-key</c> (secret: from the environment), <c>verify-url</c> (another
/// siteverify-compatible endpoint), <c>timeout-seconds</c> (10) and <c>max-token-length</c> (2048).
/// Without a secret the captcha is off.
/// </summary>
public sealed class CaptchaOptions
{
    /// <summary>Config section of the captcha settings.</summary>
    public const string ConfigPath = "altruist:security:captcha";

    /// <summary>Built-in siteverify endpoints by provider name (case-insensitive).</summary>
    public static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["turnstile"] = "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        ["hcaptcha"] = "https://api.hcaptcha.com/siteverify",
        ["recaptcha"] = "https://www.google.com/recaptcha/api/siteverify",
    };

    /// <summary>Provider name (<c>provider</c>, default <c>turnstile</c>); picks <see cref="VerifyUrl"/> unless <c>verify-url</c> is set.</summary>
    public string Provider { get; set; } = "turnstile";
    /// <summary>Public widget key for clients (<c>site-key</c>).</summary>
    public string? SiteKey { get; set; }
    /// <summary>Server secret (<c>secret-key</c>; supply it from the environment, not a committed file). Null disables the captcha.</summary>
    public string? SecretKey { get; set; }
    /// <summary>The siteverify endpoint posted to.</summary>
    public string VerifyUrl { get; set; } = Endpoints["turnstile"];
    /// <summary>Per-verification timeout (<c>timeout-seconds</c>, default 10 seconds); a timeout rejects the token.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Longer tokens are rejected without a call (<c>max-token-length</c>, default 2048 characters).</summary>
    public int MaxTokenLength { get; set; } = 2048;

    /// <summary>A secret is configured: tokens are verified.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>Reads <see cref="ConfigPath"/>; missing keys keep their defaults.</summary>
    /// <exception cref="ArgumentException">When <c>provider</c> is unknown and no <c>verify-url</c> is given.</exception>
    public static CaptchaOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new CaptchaOptions();
        options.Provider = TokenConfig.Text(section, "provider")?.ToLowerInvariant() ?? options.Provider;
        options.SiteKey = TokenConfig.Text(section, "site-key");
        options.SecretKey = TokenConfig.Text(section, "secret-key");
        options.VerifyUrl = TokenConfig.Text(section, "verify-url")
                            ?? (Endpoints.TryGetValue(options.Provider, out var url) ? url
                                : throw new ArgumentException($"{ConfigPath}:provider must be turnstile, hcaptcha or recaptcha (or set verify-url), not '{options.Provider}'."));
        if (TokenConfig.Number(section, "timeout-seconds") is { } timeout)
            options.Timeout = TimeSpan.FromSeconds(timeout);
        if (TokenConfig.Number(section, "max-token-length") is { } max)
            options.MaxTokenLength = (int)max;
        return options;
    }
}

/// <summary>
/// A human check (captcha) for sensitive anonymous actions such as registration. Default:
/// <see cref="TurnstileCaptchaVerifier"/> (singleton), configured under <c>altruist:security:captcha</c>. Expose
/// <see cref="SiteKey"/> to your client and verify the widget's response token server-side before acting.
/// </summary>
/// <example>
/// <code>
/// if (captcha.Enabled &amp;&amp; !await captcha.VerifyAsync(body.CaptchaToken, HttpContext.ClientIp()))
///     return BadRequest("captcha");
/// </code>
/// </example>
public interface ICaptchaVerifier
{
    /// <summary>False when no captcha is configured: callers skip the check.</summary>
    bool Enabled { get; }

    /// <summary>The public site key for the client's widget while <see cref="Enabled"/>, else null.</summary>
    string? SiteKey { get; }

    /// <summary>True when <paramref name="token"/> is a fresh, valid response for this site (always true while disabled).</summary>
    /// <param name="token">The response token from the client's widget.</param>
    /// <param name="remoteIp">The client's address (e.g. <c>HttpContext.ClientIp()</c>), or null.</param>
    /// <param name="cancellationToken">Cancels the verification.</param>
    Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-side captcha validation against a siteverify endpoint (Cloudflare Turnstile by default):
/// POST <c>secret</c>, <c>response</c> and <c>remoteip</c>, accept on <c>"success": true</c>. Fails
/// closed: an unreachable or failing endpoint rejects the token (logged). Empty and over-long
/// tokens are rejected without a call.
/// </summary>
[Service(typeof(ICaptchaVerifier))]
public sealed class TurnstileCaptchaVerifier : ICaptchaVerifier
{
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient _http;
    private readonly ILogger _log;

    /// <summary>DI constructor: options from <see cref="CaptchaOptions.ConfigPath"/>.</summary>
    [ActivatorUtilitiesConstructor]
    public TurnstileCaptchaVerifier(ILoggerFactory loggerFactory)
        : this(CaptchaOptions.FromConfiguration(AppConfigLoader.Load()), null, loggerFactory) { }

    /// <summary>Creates a verifier with explicit options.</summary>
    /// <param name="options">The captcha settings.</param>
    /// <param name="http">The client to post with (tests); default a shared one with the options' timeout.</param>
    /// <param name="loggerFactory">Logger factory; optional.</param>
    public TurnstileCaptchaVerifier(CaptchaOptions options, HttpClient? http = null, ILoggerFactory? loggerFactory = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? SharedHttp;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TurnstileCaptchaVerifier>();
    }

    /// <summary>The settings in effect.</summary>
    public CaptchaOptions Options { get; }

    /// <inheritdoc/>
    public bool Enabled => Options.Enabled;

    /// <inheritdoc/>
    public string? SiteKey => Options.Enabled ? Options.SiteKey : null;

    /// <inheritdoc/>
    /// <remarks>Returns false on endpoint errors and timeouts; throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled. A <paramref name="remoteIp"/> of <c>"unknown"</c> is not sent.</remarks>
    public async Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
            return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > Options.MaxTokenLength)
            return false;
        var form = new Dictionary<string, string> { ["secret"] = Options.SecretKey!, ["response"] = token };
        if (!string.IsNullOrEmpty(remoteIp) && remoteIp != "unknown")
            form["remoteip"] = remoteIp;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.Timeout);
        try
        {
            using var response = await _http.PostAsync(Options.VerifyUrl, new FormUrlEncodedContent(form), timeout.Token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(timeout.Token).ConfigureAwait(false);
            if (result?.Success == true)
                return true;
            _log.LogInformation("Captcha rejected a token ({Codes})", string.Join(",", result?.ErrorCodes ?? Array.Empty<string>()));
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Captcha siteverify failed; the token is rejected");
            return false;
        }
    }

    private sealed class SiteVerifyResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("error-codes")] public string[]? ErrorCodes { get; set; }
    }
}
