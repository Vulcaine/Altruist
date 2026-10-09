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
/// <param name="To">Recipient address (normalize with <see cref="EmailAddress.Normalize"/> first).</param>
/// <param name="Subject">Subject line.</param>
/// <param name="Html">HTML body.</param>
/// <param name="Text">Plain-text body; also what <see cref="LogEmailSender"/> writes to the log.</param>
public sealed record EmailMessage(string To, string Subject, string Html, string Text);

/// <summary>Delivers transactional emails. Throws when delivery failed.</summary>
/// <remarks>
/// Inject this; the DI registration is <see cref="ConfiguredEmailSender"/> (singleton), which picks Resend or
/// log-only delivery from <c>altruist:email</c>. Inside a request handler prefer
/// <see cref="EmailSenderExtensions.SendInBackground(IEmailSender, EmailMessage, ILogger)"/> so the caller never waits on the provider.
/// </remarks>
/// <example>
/// <code>
/// await emailSender.SendAsync(new EmailMessage(to, "Verify your email", html, text), ct);
/// </code>
/// </example>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/>; completes when the provider accepted it.</summary>
    /// <param name="message">The email to send.</param>
    /// <param name="cancellationToken">Cancels the delivery attempt.</param>
    /// <exception cref="HttpRequestException">The provider rejected the email (Resend implementation).</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>altruist:email</c>: <c>provider</c> (<c>log</c>, the default: emails are written to the log
/// instead of sent; or <c>resend</c>), <c>from</c> (the sender; with Resend on a verified domain)
/// and <c>resend-api-key</c> (secret: from the environment). <c>resend</c> without a key logs too.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>Config section: <c>altruist:email</c>.</summary>
    public const string ConfigPath = "altruist:email";
    /// <summary>Provider value that logs emails instead of sending them (default).</summary>
    public const string ProviderLog = "log";
    /// <summary>Provider value that sends through Resend (needs <see cref="ResendApiKey"/>).</summary>
    public const string ProviderResend = "resend";

    /// <summary><c>altruist:email:provider</c>, lower-cased: <see cref="ProviderLog"/> or <see cref="ProviderResend"/>.</summary>
    public string Provider { get; set; } = ProviderLog;
    /// <summary><c>altruist:email:from</c>: sender address; defaults to <c>no-reply@localhost</c>.</summary>
    public string From { get; set; } = "no-reply@localhost";
    /// <summary><c>altruist:email:resend-api-key</c>: Resend API key (secret; supply via environment, not a committed file).</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>Emails really go out (Resend with a key); otherwise they are logged.</summary>
    public bool SendsRealEmail => Provider == ProviderResend && !string.IsNullOrWhiteSpace(ResendApiKey);

    /// <summary>Reads <c>altruist:email</c> from <paramref name="configuration"/>; missing keys keep their defaults.</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The parsed options.</returns>
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

    /// <summary>DI constructor: reads <c>altruist:email</c> via <c>AppConfigLoader.Load()</c> (the app config files), not the injected <see cref="IConfiguration"/>.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    [ActivatorUtilitiesConstructor]
    public ConfiguredEmailSender(ILoggerFactory loggerFactory)
        : this(EmailOptions.FromConfiguration(AppConfigLoader.Load()), loggerFactory) { }

    /// <summary>Creates a sender from explicit options (tests, tools); picks Resend or log delivery as described on the class.</summary>
    /// <param name="options">Email options.</param>
    /// <param name="loggerFactory">Logger factory; <c>null</c> disables logging.</param>
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

    /// <summary>The options this sender was built from (check <see cref="EmailOptions.SendsRealEmail"/> to tell users whether mail really goes out).</summary>
    public EmailOptions Options { get; }

    /// <inheritdoc/>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => _inner.SendAsync(message, cancellationToken);
}

/// <summary>Resend (https://resend.com) HTTP API: POST /emails with a Bearer API key.</summary>
/// <remarks>Normally created by <see cref="ConfiguredEmailSender"/>; construct it directly only to bypass config.</remarks>
public sealed class ResendEmailSender : IEmailSender
{
    /// <summary>Resend send-email endpoint.</summary>
    public const string Endpoint = "https://api.resend.com/emails";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _from;

    /// <summary>Creates a Resend sender.</summary>
    /// <param name="apiKey">Resend API key.</param>
    /// <param name="from">Sender address (must be on a domain verified in Resend).</param>
    /// <param name="http">HTTP client to use; defaults to a shared client with a 15 s timeout.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apiKey"/> or <paramref name="from"/> is <c>null</c>.</exception>
    public ResendEmailSender(string apiKey, string from, HttpClient? http = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _from = from ?? throw new ArgumentNullException(nameof(from));
        _http = http ?? SharedHttp;
    }

    /// <inheritdoc/>
    /// <exception cref="HttpRequestException">Resend returned a non-success status (message includes up to 300 characters of the response body).</exception>
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

    /// <summary>Creates a log-only sender.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    public LogEmailSender(ILoggerFactory loggerFactory) => _log = loggerFactory.CreateLogger<LogEmailSender>();

    /// <inheritdoc/>
    /// <remarks>Logs recipient, subject and the plain-text body at Information level; never fails.</remarks>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _log.LogInformation("Email not sent (no email provider configured) to {To}: {Subject}\n{Text}", message.To, message.Subject, message.Text);
        return Task.CompletedTask;
    }
}

/// <summary>Fire-and-forget helpers for <see cref="IEmailSender"/>.</summary>
public static class EmailSenderExtensions
{
    /// <summary>
    /// Sends on the thread pool and returns at once, so a request never waits for the mail
    /// provider; a failure goes to <paramref name="onFailure"/> (never thrown). The returned task
    /// completes when the attempt is over.
    /// </summary>
    /// <remarks>Use from request handlers (sign-up, password reset) where the response must not depend on mail delivery;
    /// await <see cref="IEmailSender.SendAsync"/> directly when the caller must know the outcome. No cancellation token is passed to the send.</remarks>
    /// <param name="sender">The sender.</param>
    /// <param name="message">The email.</param>
    /// <param name="onFailure">Called with the delivery exception; its own exceptions are swallowed.</param>
    /// <returns>A task that completes (never faults) when the attempt finishes.</returns>
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
    /// <param name="sender">The sender.</param>
    /// <param name="message">The email.</param>
    /// <param name="logger">Logger for failures.</param>
    /// <returns>A task that completes (never faults) when the attempt finishes.</returns>
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
    /// <param name="email">Raw user input.</param>
    public static string? Normalize(string? email)
    {
        var e = email?.Trim();
        return string.IsNullOrEmpty(e) ? null : e.ToLowerInvariant();
    }

    /// <summary>Validates a normalized address (see <see cref="Normalize"/>): at most 254 characters, a local part of at most 64.</summary>
    /// <remarks>Expects lower-case input; an un-normalized address with upper-case letters fails.</remarks>
    /// <param name="normalized">Output of <see cref="Normalize"/>.</param>
    public static bool IsValid(string? normalized) =>
        normalized is { Length: > 0 and <= MaxLength }
        && normalized.IndexOf('@') is > 0 and <= LocalMaxLength
        && Pattern().IsMatch(normalized);
}
