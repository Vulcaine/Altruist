/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;

using Altruist.Security;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using MimeKit;

namespace Altruist.Email;

/// <summary>How the SMTP connection is encrypted (<c>altruist:email:smtp:security</c>).</summary>
public enum SmtpSecurity
{
    /// <summary><c>starttls</c> (default): plain connect, then a mandatory STARTTLS upgrade before authenticating; the send
    /// fails if the server does not offer it (no silent downgrade). The usual choice, on port 587.</summary>
    StartTls,
    /// <summary><c>ssl</c>: implicit TLS from the first byte (SMTPS), usually on port 465.</summary>
    Ssl,
    /// <summary><c>none</c>: no encryption, credentials and mail in clear text. Development only (a local catcher such as
    /// Mailpit or MailHog); <see cref="EmailOptions.Validate(string)"/> rejects it in every other environment.</summary>
    None,
}

/// <summary>
/// <c>altruist:email:smtp</c>: the SMTP server used when <c>altruist:email:provider</c> is <c>smtp</c>.
/// </summary>
/// <remarks>
/// <para>Keys (kebab-case; each value may be a <c>${NAME}</c> / <c>${NAME:-default}</c> environment placeholder):
/// <c>host</c>, <c>port</c> (587), <c>username</c>, <c>password</c> (secret: from the environment), <c>from</c> (sender
/// address, falls back to <c>altruist:email:from</c>), <c>from-name</c> (display name, optional), <c>security</c>
/// (<c>starttls</c> default, <c>ssl</c>, <c>none</c>: see <see cref="SmtpSecurity"/>), <c>timeout-seconds</c> (30, per
/// network operation).</para>
/// <para>Use SMTP when your mail provider is reachable only over SMTP or you already hold SMTP credentials; prefer
/// <c>resend</c> for Resend (HTTP API) and <c>log</c> for development. Without <c>username</c> no authentication is attempted
/// (an open relay inside your network); Production requires credentials.</para>
/// </remarks>
/// <example>
/// <code>
/// altruist:
///   email:
///     provider: smtp
///     smtp:
///       host: ${SMTP_HOST}
///       port: ${SMTP_PORT:-587}
///       username: ${SMTP_USER}
///       password: ${SMTP_PASS}
///       from: ${SMTP_FROM}
///       from-name: My Game
/// </code>
/// </example>
public sealed class SmtpOptions
{
    /// <summary>Default submission port (STARTTLS).</summary>
    public const int DefaultPort = 587;
    /// <summary>Default timeout per network operation, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary><c>host</c>: SMTP server host name; without it the <c>smtp</c> provider cannot send.</summary>
    public string? Host { get; set; }
    /// <summary><c>port</c>: server port; <see cref="DefaultPort"/> (587) by default, usually 465 with <see cref="SmtpSecurity.Ssl"/>.</summary>
    public int Port { get; set; } = DefaultPort;
    /// <summary><c>username</c>: login; when unset no authentication is attempted.</summary>
    public string? Username { get; set; }
    /// <summary><c>password</c>: secret; supply via the environment (<c>${SMTP_PASS}</c>), never a committed file. Never logged.</summary>
    public string? Password { get; set; }
    /// <summary><c>from</c>: sender address (or <c>Name &lt;address&gt;</c>); when unset <c>altruist:email:from</c> is used.</summary>
    public string? From { get; set; }
    /// <summary><c>from-name</c>: sender display name; overrides a name given in the sender address.</summary>
    public string? FromName { get; set; }
    /// <summary><c>security</c>: connection encryption, <see cref="SmtpSecurity.StartTls"/> by default.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    /// <summary><c>timeout-seconds</c>: timeout per network operation (connect, each command); <see cref="DefaultTimeoutSeconds"/> by default.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Reads the keys listed on the class from <paramref name="section"/> (normally <c>altruist:email:smtp</c>).</summary>
    /// <param name="section">The <c>smtp</c> section.</param>
    /// <param name="strict">When true an unrecognized <c>port</c>, <c>timeout-seconds</c> or <c>security</c> throws; when false it keeps the default.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="strict"/> and a value is not recognized.</exception>
    public static SmtpOptions FromSection(IConfigurationSection section, bool strict = true)
    {
        ArgumentNullException.ThrowIfNull(section);
        var o = new SmtpOptions
        {
            Host = TokenConfig.Text(section, "host"),
            Username = TokenConfig.Text(section, "username"),
            Password = TokenConfig.Text(section, "password"),
            From = TokenConfig.Text(section, "from"),
            FromName = TokenConfig.Text(section, "from-name"),
        };
        o.Port = Int(section, "port", DefaultPort, strict);
        o.TimeoutSeconds = Int(section, "timeout-seconds", DefaultTimeoutSeconds, strict);
        var security = TokenConfig.Text(section, "security")?.ToLowerInvariant();
        o.Security = security switch
        {
            null or "starttls" => SmtpSecurity.StartTls,
            "ssl" => SmtpSecurity.Ssl,
            "none" => SmtpSecurity.None,
            _ when strict => throw new InvalidOperationException(
                $"{section.Path}:security '{security}' is not recognized; use 'starttls', 'ssl' or 'none'."),
            _ => SmtpSecurity.StartTls,
        };
        return o;
    }

    /// <summary>Host, port, security and user for diagnostics; the password is never included.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() =>
        $"smtp://{Username ?? "(no auth)"}@{Host}:{Port} ({Security}, password {(Password is null ? "unset" : "set")})";

    private static int Int(IConfigurationSection section, string key, int fallback, bool strict)
    {
        var text = TokenConfig.Text(section, key);
        if (text is null)
            return fallback;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        return strict ? throw new InvalidOperationException($"{section.Path}:{key} '{text}' is not a whole number.") : fallback;
    }
}

/// <summary>
/// Sends through any SMTP server with MailKit: one connection per email (connect, encrypt, authenticate, send, quit),
/// a multipart/alternative message with the plain-text and HTML bodies of <see cref="EmailMessage"/>.
/// </summary>
/// <remarks>
/// <para>Normally created by <see cref="ConfiguredEmailSender"/> from <c>altruist:email:provider: smtp</c> (see
/// <see cref="SmtpOptions"/> for the keys); construct it directly only to bypass config. Thread-safe: every send uses its
/// own connection, which suits transactional volume (sign-up, password reset), not bulk mail.</para>
/// <para>Logs only the recipient's domain and the subject (Debug), never the password, the full address or the body.</para>
/// </remarks>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;
    private readonly SecureSocketOptions _socketOptions;
    private readonly int _timeoutMs;
    private readonly MailboxAddress _from;
    private readonly ILogger<SmtpEmailSender> _log;

    /// <summary>Creates an SMTP sender from a snapshot of <paramref name="options"/> (later changes to it are not seen).</summary>
    /// <param name="options">Server settings; <see cref="SmtpOptions.Host"/> is required. <see cref="SmtpOptions.From"/> is ignored here: pass the sender as <paramref name="from"/>.</param>
    /// <param name="from">Sender (<c>no-reply@example.com</c> or <c>Game &lt;no-reply@example.com&gt;</c>), e.g. <see cref="EmailOptions.SmtpSender"/>.</param>
    /// <param name="loggerFactory">Logger factory; <c>null</c> disables logging.</param>
    /// <exception cref="ArgumentException">No host, a bad port or timeout.</exception>
    /// <exception cref="FormatException"><paramref name="from"/> is not a valid address.</exception>
    public SmtpEmailSender(SmtpOptions options, string from, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(from);
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new ArgumentException("SMTP host is required.", nameof(options));
        if (options.Port is < 1 or > 65535)
            throw new ArgumentException($"SMTP port {options.Port} is out of range.", nameof(options));
        if (options.TimeoutSeconds <= 0)
            throw new ArgumentException("SMTP timeout must be positive.", nameof(options));
        _host = options.Host.Trim();
        _port = options.Port;
        _username = string.IsNullOrWhiteSpace(options.Username) ? null : options.Username;
        _password = options.Password;
        _timeoutMs = checked(options.TimeoutSeconds * 1000);
        _socketOptions = options.Security switch
        {
            SmtpSecurity.Ssl => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        _from = ParseSender(from, null);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SmtpEmailSender>();
    }

    /// <summary>The sender every message is sent from.</summary>
    public string From => _from.ToString();

    /// <summary>
    /// Builds the MIME message that <see cref="SendAsync"/> sends: From, To, Subject, Date, Message-Id and a
    /// multipart/alternative body (text/plain first, then text/html); only one part when the other body is empty.
    /// </summary>
    /// <param name="message">The email.</param>
    /// <returns>A new message; dispose it when done.</returns>
    /// <exception cref="ParseException"><see cref="EmailMessage.To"/> is not a valid address.</exception>
    public MimeMessage CreateMessage(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var mime = new MimeMessage();
        mime.From.Add(_from.Clone());
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            TextBody = string.IsNullOrEmpty(message.Text) ? null : message.Text,
            HtmlBody = string.IsNullOrEmpty(message.Html) ? null : message.Html,
        }.ToMessageBody();
        return mime;
    }

    /// <inheritdoc/>
    /// <remarks>Connects, upgrades or opens TLS per <see cref="SmtpOptions.Security"/>, authenticates when a username is set,
    /// sends and quits; a failed QUIT after the server accepted the message does not fail the send. Every network operation
    /// is bounded by <see cref="SmtpOptions.TimeoutSeconds"/>; <paramref name="cancellationToken"/> aborts the attempt at any point
    /// before the server accepted the message.</remarks>
    /// <exception cref="SmtpCommandException">The server rejected the sender, recipient or message.</exception>
    /// <exception cref="AuthenticationException">The credentials were rejected.</exception>
    /// <exception cref="NotSupportedException"><see cref="SmtpSecurity.StartTls"/> but the server does not offer STARTTLS.</exception>
    /// <exception cref="SslHandshakeException">TLS failed (e.g. an untrusted certificate).</exception>
    /// <exception cref="TimeoutException">A network operation took longer than <see cref="SmtpOptions.TimeoutSeconds"/>.</exception>
    /// <exception cref="ParseException"><see cref="EmailMessage.To"/> is not a valid address.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mime = CreateMessage(message);
        using var client = new SmtpClient { Timeout = _timeoutMs };
        await client.ConnectAsync(_host, _port, _socketOptions, cancellationToken).ConfigureAwait(false);
        if (_username is not null)
            await client.AuthenticateAsync(_username, _password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        // MailKit ignores a failed or cancelled QUIT: the server already accepted the message.
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        _log.LogDebug("Email \"{Subject}\" sent via SMTP to a recipient at {Domain}", message.Subject, RecipientDomain(message.To));
    }

    /// <summary>Combines a sender address and an optional display name into the sender string (<c>Name &lt;address&gt;</c>).</summary>
    /// <param name="from">Address, optionally already with a name (<c>Game &lt;no-reply@example.com&gt;</c>).</param>
    /// <param name="fromName">Display name that replaces any name in <paramref name="from"/>; null or blank keeps it.</param>
    /// <returns>The formatted sender.</returns>
    /// <exception cref="FormatException"><paramref name="from"/> is not a valid address with a domain.</exception>
    public static string FormatSender(string from, string? fromName) => ParseSender(from, fromName).ToString();

    private static MailboxAddress ParseSender(string from, string? fromName)
    {
        if (!MailboxAddress.TryParse(from?.Trim() ?? string.Empty, out var mailbox) || !mailbox.Address.Contains('@'))
            throw new FormatException($"{EmailOptions.ConfigPath} sender '{from}' is not a valid email address.");
        if (!string.IsNullOrWhiteSpace(fromName))
            mailbox.Name = fromName.Trim();
        return mailbox;
    }

    private static string RecipientDomain(string to)
    {
        var at = to.LastIndexOf('@');
        return at >= 0 ? to[(at + 1)..].Trim().TrimEnd('>') : "(no domain)";
    }
}
