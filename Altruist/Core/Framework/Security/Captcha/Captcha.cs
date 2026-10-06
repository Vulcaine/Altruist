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
    public const string ConfigPath = "altruist:security:captcha";

    public static readonly IReadOnlyDictionary<string, string> Endpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["turnstile"] = "https://challenges.cloudflare.com/turnstile/v0/siteverify",
        ["hcaptcha"] = "https://api.hcaptcha.com/siteverify",
        ["recaptcha"] = "https://www.google.com/recaptcha/api/siteverify",
    };

    public string Provider { get; set; } = "turnstile";
    public string? SiteKey { get; set; }
    public string? SecretKey { get; set; }
    public string VerifyUrl { get; set; } = Endpoints["turnstile"];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxTokenLength { get; set; } = 2048;

    /// <summary>A secret is configured: tokens are verified.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);

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

/// <summary>A human check (captcha) for sensitive anonymous actions such as registration.</summary>
public interface ICaptchaVerifier
{
    /// <summary>False when no captcha is configured: callers skip the check.</summary>
    bool Enabled { get; }

    /// <summary>The public site key for the client's widget while <see cref="Enabled"/>, else null.</summary>
    string? SiteKey { get; }

    /// <summary>True when <paramref name="token"/> is a fresh, valid response for this site (always true while disabled).</summary>
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

    [ActivatorUtilitiesConstructor]
    public TurnstileCaptchaVerifier(ILoggerFactory loggerFactory)
        : this(CaptchaOptions.FromConfiguration(AppConfigLoader.Load()), null, loggerFactory) { }

    /// <param name="http">The client to post with (tests); default a shared one with the options' timeout.</param>
    public TurnstileCaptchaVerifier(CaptchaOptions options, HttpClient? http = null, ILoggerFactory? loggerFactory = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _http = http ?? SharedHttp;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TurnstileCaptchaVerifier>();
    }

    public CaptchaOptions Options { get; }

    public bool Enabled => Options.Enabled;

    public string? SiteKey => Options.Enabled ? Options.SiteKey : null;

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
