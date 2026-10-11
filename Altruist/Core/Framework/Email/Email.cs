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
/// Inject this; the DI registration is <see cref="ConfiguredEmailSender"/> (singleton), which picks log-only,
/// Resend or SMTP delivery from <c>altruist:email</c> (see <see cref="EmailOptions"/>). Inside a request handler prefer
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
    /// <exception cref="MailKit.Net.Smtp.SmtpCommandException">The SMTP server rejected the email (SMTP implementation; see <see cref="SmtpEmailSender.SendAsync"/> for the other SMTP failures).</exception>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>altruist:email</c>: which provider delivers transactional email and as whom.
/// </summary>
/// <remarks>
/// <para>Providers (<c>altruist:email:provider</c>):</para>
/// <list type="bullet">
/// <item><c>log</c> (default): nothing is sent; each email (links included) is written to the log. Use in development and tests.</item>
/// <item><c>resend</c>: the Resend HTTP API (<c>resend-api-key</c>). Use when you have a Resend account; no SMTP ports needed.
/// Without a key it falls back to logging (with a warning).</item>
/// <item><c>smtp</c>: any SMTP server or relay (your mail host, Amazon SES / Postmark / Mailgun / SendGrid SMTP endpoints,
/// a company relay) via <see cref="SmtpEmailSender"/>. Use when the provider has no HTTP API you want to depend on, or you
/// already have SMTP credentials. Keys under <c>altruist:email:smtp</c>, see <see cref="SmtpOptions"/>.</item>
/// </list>
/// <para><c>from</c> is the sender (<c>no-reply@example.com</c> or <c>Game &lt;no-reply@example.com&gt;</c>); with Resend it
/// must be on a verified domain, with SMTP <c>smtp:from</c> / <c>smtp:from-name</c> override it.</para>
/// <para>Secrets (<c>resend-api-key</c>, <c>smtp:password</c>) belong in the environment: every value may be a
/// <c>${NAME}</c> / <c>${NAME:-default}</c> placeholder (expanded by <c>AppConfigLoader</c>).</para>
/// <para>Validation (<see cref="Validate(string)"/>, run at startup by <see cref="ConfiguredEmailSender"/>): with <c>smtp</c>
/// in Production, <c>host</c>, a sender, <c>username</c> and <c>password</c> are required; <c>security: none</c> is accepted
/// only in Development. Anything else unfit stops the startup with an <see cref="InvalidOperationException"/>.</para>
/// </remarks>
/// <example>
/// <code>
/// altruist:
///   email:
///     provider: smtp
///     from: "Game &lt;no-reply@example.com&gt;"
///     smtp:
///       host: ${SMTP_HOST}
///       port: ${SMTP_PORT:-587}
///       username: ${SMTP_USER}
///       password: ${SMTP_PASS}
///       security: starttls          # starttls (default) | ssl (implicit TLS, port 465) | none (Development only)
///       timeout-seconds: 30
/// </code>
/// </example>
public sealed class EmailOptions
{
    /// <summary>Config section: <c>altruist:email</c>.</summary>
    public const string ConfigPath = "altruist:email";
    /// <summary>Provider value that logs emails instead of sending them (default).</summary>
    public const string ProviderLog = "log";
    /// <summary>Provider value that sends through Resend (needs <see cref="ResendApiKey"/>).</summary>
    public const string ProviderResend = "resend";
    /// <summary>Provider value that sends through an SMTP server (needs <see cref="SmtpOptions.Host"/>; see <see cref="Smtp"/>).</summary>
    public const string ProviderSmtp = "smtp";
    /// <summary>The sender used when <c>altruist:email:from</c> is not set; not accepted as an SMTP sender in Production.</summary>
    public const string DefaultFrom = "no-reply@localhost";

    /// <summary><c>altruist:email:provider</c>, lower-cased: <see cref="ProviderLog"/>, <see cref="ProviderResend"/> or <see cref="ProviderSmtp"/>.</summary>
    public string Provider { get; set; } = ProviderLog;
    /// <summary><c>altruist:email:from</c>: sender address, optionally with a display name (<c>Game &lt;no-reply@example.com&gt;</c>); defaults to <see cref="DefaultFrom"/>.</summary>
    public string From { get; set; } = DefaultFrom;
    /// <summary><c>altruist:email:resend-api-key</c>: Resend API key (secret; supply via environment, not a committed file).</summary>
    public string? ResendApiKey { get; set; }
    /// <summary><c>altruist:email:smtp</c>: SMTP settings, used when <see cref="Provider"/> is <see cref="ProviderSmtp"/>.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Emails really go out (Resend with a key, or SMTP with a host); otherwise they are logged.</summary>
    public bool SendsRealEmail => Provider switch
    {
        ProviderResend => !string.IsNullOrWhiteSpace(ResendApiKey),
        ProviderSmtp => !string.IsNullOrWhiteSpace(Smtp.Host),
        _ => false,
    };

    /// <summary>The SMTP sender: <see cref="SmtpOptions.From"/> when set, else <see cref="From"/>, with <see cref="SmtpOptions.FromName"/> (when set) as the display name.</summary>
    /// <returns>The sender as <c>Name &lt;address&gt;</c> or a bare address.</returns>
    /// <exception cref="FormatException">The configured sender is not a valid address.</exception>
    public string SmtpSender() => SmtpEmailSender.FormatSender(Smtp.From ?? From, Smtp.FromName);

    /// <summary>Reads <c>altruist:email</c> from <paramref name="configuration"/>; missing keys keep their defaults.</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="InvalidOperationException">The provider is <c>smtp</c> and <c>smtp:port</c>, <c>smtp:timeout-seconds</c> or <c>smtp:security</c> is not a recognized value.</exception>
    public static EmailOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new EmailOptions();
        options.Provider = TokenConfig.Text(section, "provider")?.ToLowerInvariant() ?? options.Provider;
        options.From = TokenConfig.Text(section, "from") ?? options.From;
        options.ResendApiKey = TokenConfig.Text(section, "resend-api-key");
        var smtp = SmtpOptions.FromSection(section.GetSection("smtp"), strict: options.Provider == ProviderSmtp);
        options.Smtp = smtp;
        return options;
    }

    /// <summary>
    /// Checks that the configured provider can work in <paramref name="environmentName"/>; does nothing for <c>log</c> and <c>resend</c>.
    /// For <c>smtp</c>: in Production <c>host</c>, a sender (<c>smtp:from</c> or <c>from</c>, not <see cref="DefaultFrom"/>),
    /// <c>username</c> and <c>password</c> are required; <c>security: none</c> is accepted only in Development; the sender must be
    /// a valid address; the port must be 1..65535 and the timeout positive.
    /// </summary>
    /// <param name="environmentName">Environment name (<c>Production</c>, <c>Staging</c>, <c>Development</c>...), compared case-insensitively.</param>
    /// <exception cref="InvalidOperationException">Lists every problem found, naming the config keys.</exception>
    public void Validate(string environmentName)
    {
        if (Provider != ProviderSmtp)
            return;
        var production = string.Equals(environmentName?.Trim(), AltruistEnvironment.Production, StringComparison.OrdinalIgnoreCase);
        var development = string.Equals(environmentName?.Trim(), AltruistEnvironment.Development, StringComparison.OrdinalIgnoreCase);
        var smtp = $"{ConfigPath}:smtp";
        var problems = new List<string>();
        if (production)
        {
            if (string.IsNullOrWhiteSpace(Smtp.Host)) problems.Add($"{smtp}:host is not set");
            if (Smtp.From is null && From == DefaultFrom) problems.Add($"no sender: set {smtp}:from or {ConfigPath}:from");
            if (string.IsNullOrWhiteSpace(Smtp.Username)) problems.Add($"{smtp}:username is not set");
            if (string.IsNullOrWhiteSpace(Smtp.Password)) problems.Add($"{smtp}:password is not set");
        }
        if (Smtp.Security == SmtpSecurity.None && !development)
            problems.Add($"{smtp}:security 'none' (unencrypted) is only allowed in Development; use 'starttls' or 'ssl'");
        if (Smtp.Port is < 1 or > 65535) problems.Add($"{smtp}:port {Smtp.Port} is out of range (1..65535)");
        if (Smtp.TimeoutSeconds <= 0) problems.Add($"{smtp}:timeout-seconds must be positive");
        try { SmtpSender(); }
        catch (FormatException ex) { problems.Add(ex.Message); }
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"{ConfigPath}:provider 'smtp' is not usable in {environmentName}: {string.Join("; ", problems)}.");
    }
}

/// <summary>
/// The configured <see cref="IEmailSender"/>: <see cref="SmtpEmailSender"/> when <c>provider</c> is <c>smtp</c> with a host,
/// <see cref="ResendEmailSender"/> when it is <c>resend</c> with an API key, otherwise <see cref="LogEmailSender"/> (with a
/// warning for a <c>resend</c> / <c>smtp</c> that cannot send or an unknown provider).
/// </summary>
/// <remarks>
/// The options are checked with <see cref="EmailOptions.Validate(string)"/>: the DI instance reports an unfit configuration
/// from its <see cref="PostConstructAttribute"/> hook, which stops the startup (and every send throws the same error);
/// the explicit-options constructor throws at once.
/// </remarks>
[Service(typeof(IEmailSender))]
public sealed class ConfiguredEmailSender : IEmailSender
{
    private readonly IEmailSender _inner;
    private readonly InvalidOperationException? _configurationError;

    /// <summary>DI constructor: reads <c>altruist:email</c> via <c>AppConfigLoader.Load()</c> (the app config files), not the injected <see cref="IConfiguration"/>, for the current <see cref="AltruistEnvironment"/>.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    [ActivatorUtilitiesConstructor]
    public ConfiguredEmailSender(ILoggerFactory loggerFactory)
    {
        // A throwing constructor would only be logged by the DI warm-up; keep the error and throw it from the
        // [PostConstruct] hook, which does stop the startup.
        EmailOptions? options = null;
        try
        {
            options = EmailOptions.FromConfiguration(AppConfigLoader.Load());
            options.Validate(AltruistEnvironment.Name);
        }
        catch (InvalidOperationException ex)
        {
            _configurationError = ex;
            Options = options ?? new EmailOptions();
            _inner = new UnusableSender(ex);
            return;
        }
        Options = options;
        _inner = Create(options, loggerFactory);
    }

    /// <summary>Creates a sender from explicit options (tests, tools); picks the delivery as described on the class.</summary>
    /// <param name="options">Email options.</param>
    /// <param name="loggerFactory">Logger factory; <c>null</c> disables logging.</param>
    /// <param name="environmentName">Environment to validate against; <c>null</c> uses <see cref="AltruistEnvironment.Name"/>.</param>
    /// <exception cref="InvalidOperationException"><see cref="EmailOptions.Validate(string)"/> failed.</exception>
    public ConfiguredEmailSender(EmailOptions options, ILoggerFactory? loggerFactory = null, string? environmentName = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(environmentName ?? AltruistEnvironment.Name);
        Options = options;
        _inner = Create(options, loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <summary>The options this sender was built from (check <see cref="EmailOptions.SendsRealEmail"/> to tell users whether mail really goes out).</summary>
    public EmailOptions Options { get; }

    /// <summary>The provider implementation in use (<see cref="LogEmailSender"/>, <see cref="ResendEmailSender"/> or <see cref="SmtpEmailSender"/>).</summary>
    public IEmailSender Inner => _inner;

    /// <summary>Startup check (run by the framework): throws when the configuration is unfit for this environment.</summary>
    /// <exception cref="InvalidOperationException">The <c>altruist:email</c> configuration failed validation.</exception>
    [PostConstruct]
    public void VerifyConfiguration()
    {
        if (_configurationError is not null)
            throw _configurationError;
    }

    /// <inheritdoc/>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => _inner.SendAsync(message, cancellationToken);

    private static IEmailSender Create(EmailOptions options, ILoggerFactory loggerFactory)
    {
        if (options.SendsRealEmail)
            return options.Provider == EmailOptions.ProviderSmtp
                ? new SmtpEmailSender(options.Smtp, options.SmtpSender(), loggerFactory)
                : new ResendEmailSender(options.ResendApiKey!, options.From);
        if (options.Provider != EmailOptions.ProviderLog)
            loggerFactory.CreateLogger<ConfiguredEmailSender>().LogWarning(
                "{Path}:provider '{Provider}' cannot send (unknown provider, no API key or no SMTP host); emails are written to the log", EmailOptions.ConfigPath, options.Provider);
        return new LogEmailSender(loggerFactory);
    }

    private sealed class UnusableSender(InvalidOperationException error) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.FromException(error);
    }
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
