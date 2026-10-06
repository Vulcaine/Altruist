/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using Altruist.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Email;

/// <summary>One transactional email: HTML plus a plain-text alternative.</summary>
public sealed record EmailMessage(string To, string Subject, string Html, string Text);

/// <summary>Delivers transactional emails. Throws when delivery failed.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>altruist:email</c>: <c>provider</c> (<c>log</c>, the default: emails are written to the log
/// instead of sent; or <c>resend</c>), <c>from</c> (the sender; with Resend on a verified domain)
/// and <c>resend-api-key</c> (secret: from the environment). <c>resend</c> without a key logs too.
/// </summary>
public sealed class EmailOptions
{
    public const string ConfigPath = "altruist:email";
    public const string ProviderLog = "log";
    public const string ProviderResend = "resend";

    public string Provider { get; set; } = ProviderLog;
    public string From { get; set; } = "no-reply@localhost";
    public string? ResendApiKey { get; set; }

    /// <summary>Emails really go out (Resend with a key); otherwise they are logged.</summary>
    public bool SendsRealEmail => Provider == ProviderResend && !string.IsNullOrWhiteSpace(ResendApiKey);

    public static EmailOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new EmailOptions();
        options.Provider = TokenConfig.Text(section, "provider")?.ToLowerInvariant() ?? options.Provider;
        options.From = TokenConfig.Text(section, "from") ?? options.From;
        options.ResendApiKey = TokenConfig.Text(section, "resend-api-key");
        return options;
    }
}

/// <summary>
/// The configured <see cref="IEmailSender"/>: <see cref="ResendEmailSender"/> when <c>provider</c> is
/// <c>resend</c> and an API key is set, otherwise <see cref="LogEmailSender"/> (with a warning for a
/// <c>resend</c> without key or an unknown provider).
/// </summary>
[Service(typeof(IEmailSender))]
public sealed class ConfiguredEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;

    [ActivatorUtilitiesConstructor]
    public ConfiguredEmailSender(ILoggerFactory loggerFactory)
        : this(EmailOptions.FromConfiguration(AppConfigLoader.Load()), loggerFactory) { }

    public ConfiguredEmailSender(EmailOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        loggerFactory ??= NullLoggerFactory.Instance;
        Options = options;
        if (options.SendsRealEmail)
        {
            _inner = new ResendEmailSender(options.ResendApiKey!, options.From);
            return;
        }
        if (options.Provider != EmailOptions.ProviderLog)
            loggerFactory.CreateLogger<ConfiguredEmailSender>().LogWarning(
                "{Path}:provider '{Provider}' cannot send (unknown provider or no API key); emails are written to the log", EmailOptions.ConfigPath, options.Provider);
        _inner = new LogEmailSender(loggerFactory);
    }

    public EmailOptions Options { get; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => _inner.SendAsync(message, cancellationToken);
}

/// <summary>Resend (https://resend.com) HTTP API: POST /emails with a Bearer API key.</summary>
public sealed class ResendEmailSender : IEmailSender
{
    public const string Endpoint = "https://api.resend.com/emails";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _from;

    public ResendEmailSender(string apiKey, string from, HttpClient? http = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _from = from ?? throw new ArgumentNullException(nameof(from));
        _http = http ?? SharedHttp;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                from = _from,
                to = new[] { message.To },
                subject = message.Subject,
                html = message.Html,
                text = message.Text,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Resend error bodies are short JSON ({name, message}) and never contain the API key.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Resend rejected the email: {(int)response.StatusCode} {body[..Math.Min(body.Length, 300)]}");
        }
    }
}

/// <summary>Development stand-in: writes the email (links included) to the log instead of sending it.</summary>
public sealed class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _log;

    public LogEmailSender(ILoggerFactory loggerFactory) => _log = loggerFactory.CreateLogger<LogEmailSender>();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _log.LogInformation("Email not sent (no email provider configured) to {To}: {Subject}\n{Text}", message.To, message.Subject, message.Text);
        return Task.CompletedTask;
    }
}

public static class EmailSenderExtensions
{
    /// <summary>
    /// Sends on the thread pool and returns at once, so a request never waits for the mail
    /// provider; a failure goes to <paramref name="onFailure"/> (never thrown). The returned task
    /// completes when the attempt is over.
    /// </summary>
    public static Task SendInBackground(this IEmailSender sender, EmailMessage message, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(onFailure);
        return Task.Run(async () =>
        {
            try
            {
                await sender.SendAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { onFailure(ex); } catch { /* a failing handler must not fault the send */ }
            }
        });
    }

    /// <summary><see cref="SendInBackground(IEmailSender, EmailMessage, Action{Exception})"/> that logs failures as warnings.</summary>
    public static Task SendInBackground(this IEmailSender sender, EmailMessage message, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return sender.SendInBackground(message, ex => logger.LogWarning(ex, "Email \"{Subject}\" could not be sent", message.Subject));
    }
}

/// <summary>Email address normalization and a pragmatic validity check.</summary>
public static partial class EmailAddress
{
    /// <summary>RFC 5321 path limit.</summary>
    public const int MaxLength = 254;

    private const int LocalMaxLength = 64;

    // On the normalized form: a dot-atom local part and a dotted domain of LDH labels with an
    // alphabetic TLD (IDN domains arrive as punycode from browsers' type=email inputs).
    // \z, not $: "$" also matches before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*\.[a-z]([a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Trimmed and lower-cased (the form to store and compare); null for null or blank input.</summary>
    public static string? Normalize(string? email)
    {
        var e = email?.Trim();
        return string.IsNullOrEmpty(e) ? null : e.ToLowerInvariant();
    }

    /// <summary>Validates a normalized address (see <see cref="Normalize"/>): at most 254 characters, a local part of at most 64.</summary>
    public static bool IsValid(string? normalized) =>
        normalized is { Length: > 0 and <= MaxLength }
        && normalized.IndexOf('@') is > 0 and <= LocalMaxLength
        && Pattern().IsMatch(normalized);
}
