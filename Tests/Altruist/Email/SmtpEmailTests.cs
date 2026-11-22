/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Altruist.Email;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MimeKit;

namespace Tests.Altruist.Email;

public sealed class SmtpEmailOptionsTests
{
    private static EmailOptions Options(Dictionary<string, string?> values) =>
        EmailOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static Dictionary<string, string?> Full(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["altruist:email:provider"] = "smtp",
            ["altruist:email:from"] = "Game <no-reply@example.com>",
            ["altruist:email:smtp:host"] = "smtp.example.com",
            ["altruist:email:smtp:username"] = "mailer",
            ["altruist:email:smtp:password"] = "s3cret-pass",
        };
        foreach (var (key, value) in overrides)
            values[key] = value;
        return values;
    }

    [Fact]
    public void Smtp_keys_bind_with_defaults()
    {
        var o = Options(Full());
        Assert.Equal("smtp", o.Provider);
        Assert.Equal("smtp.example.com", o.Smtp.Host);
        Assert.Equal(587, o.Smtp.Port);
        Assert.Equal(SmtpSecurity.StartTls, o.Smtp.Security);
        Assert.Equal(30, o.Smtp.TimeoutSeconds);
        Assert.Null(o.Smtp.From);
        Assert.True(o.SendsRealEmail);
        Assert.Equal("\"Game\" <no-reply@example.com>", o.SmtpSender());

        var ssl = Options(Full(
            ("altruist:email:provider", "SMTP"),
            ("altruist:email:smtp:port", "465"),
            ("altruist:email:smtp:security", "SSL"),
            ("altruist:email:smtp:timeout-seconds", "5"),
            ("altruist:email:smtp:from", "mail@game.io"),
            ("altruist:email:smtp:from-name", "My Game")));
        Assert.Equal("smtp", ssl.Provider);
        Assert.Equal(465, ssl.Smtp.Port);
        Assert.Equal(SmtpSecurity.Ssl, ssl.Smtp.Security);
        Assert.Equal(5, ssl.Smtp.TimeoutSeconds);
        Assert.Equal("\"My Game\" <mail@game.io>", ssl.SmtpSender());
    }

    [Fact]
    public void Password_never_appears_in_diagnostics()
    {
        var o = Options(Full());
        Assert.DoesNotContain("s3cret-pass", o.Smtp.ToString());
        Assert.Contains("password set", o.Smtp.ToString());
    }

    [Fact]
    public void Unrecognized_values_throw_only_for_the_smtp_provider()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options(Full(("altruist:email:smtp:security", "tls"))));
        Assert.Contains("altruist:email:smtp:security", ex.Message);
        Assert.Throws<InvalidOperationException>(() => Options(Full(("altruist:email:smtp:port", "abc"))));
        // Another provider ignores a broken smtp section.
        var log = Options(Full(("altruist:email:provider", "log"), ("altruist:email:smtp:security", "tls")));
        Assert.Equal(SmtpSecurity.StartTls, log.Smtp.Security);
    }

    [Fact]
    public void Production_requires_host_sender_and_credentials()
    {
        var o = Options(new() { ["altruist:email:provider"] = "smtp" });
        var ex = Assert.Throws<InvalidOperationException>(() => o.Validate("Production"));
        Assert.Contains("altruist:email:smtp:host", ex.Message);
        Assert.Contains("altruist:email:smtp:username", ex.Message);
        Assert.Contains("altruist:email:smtp:password", ex.Message);
        Assert.Contains("no sender", ex.Message);

        Assert.Throws<InvalidOperationException>(() => new ConfiguredEmailSender(o, environmentName: "production"));
        Options(Full()).Validate("Production"); // complete config passes
        Options(Full(("altruist:email:from", null), ("altruist:email:smtp:from", "a@example.com"))).Validate("Production");
    }

    [Fact]
    public void Unencrypted_smtp_is_allowed_only_in_development()
    {
        var none = Options(Full(("altruist:email:smtp:security", "none")));
        none.Validate("Development");
        Assert.Throws<InvalidOperationException>(() => none.Validate("Staging"));
        var ex = Assert.Throws<InvalidOperationException>(() => none.Validate("Production"));
        Assert.Contains("only allowed in Development", ex.Message);
    }

    [Fact]
    public void Outside_production_a_missing_host_falls_back_to_logging()
    {
        var o = Options(new() { ["altruist:email:provider"] = "smtp" });
        o.Validate("Staging");
        var sender = new ConfiguredEmailSender(o, environmentName: "Staging");
        Assert.False(sender.Options.SendsRealEmail);
        Assert.IsType<LogEmailSender>(sender.Inner);
        sender.VerifyConfiguration();
    }

    [Fact]
    public void Configured_smtp_builds_an_smtp_sender()
    {
        var sender = new ConfiguredEmailSender(Options(Full(("altruist:email:smtp:from-name", "My Game"))), environmentName: "Production");
        var smtp = Assert.IsType<SmtpEmailSender>(sender.Inner);
        Assert.Equal("\"My Game\" <no-reply@example.com>", smtp.From);
    }

    [Fact]
    public void Invalid_sender_and_ranges_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Options(Full(("altruist:email:smtp:from", "not-an-address"))).Validate("Development"));
        Assert.Throws<InvalidOperationException>(() => Options(Full(("altruist:email:smtp:port", "70000"))).Validate("Development"));
        Assert.Throws<InvalidOperationException>(() => Options(Full(("altruist:email:smtp:timeout-seconds", "0"))).Validate("Development"));
        Assert.Throws<ArgumentException>(() => new SmtpEmailSender(new SmtpOptions(), "a@example.com"));
        Assert.Throws<FormatException>(() => new SmtpEmailSender(new SmtpOptions { Host = "h" }, "nobody"));
    }
}

public sealed class SmtpEmailSenderTests
{
    private static SmtpEmailSender Sender(FakeSmtpServer server, SmtpSecurity security = SmtpSecurity.None, string? username = "mailer", int timeoutSeconds = 10, ILoggerFactory? loggerFactory = null) =>
        new(new SmtpOptions { Host = "127.0.0.1", Port = server.Port, Username = username, Password = "s3cret-pass", Security = security, TimeoutSeconds = timeoutSeconds },
            "Game <no-reply@example.com>", loggerFactory);

    private static EmailMessage Message(string to = "a@b.io") => new(to, "S", "<p>h</p>", "t");

    [Fact]
    public void Message_has_sender_subject_and_text_plus_html_alternatives()
    {
        var sender = new SmtpEmailSender(new SmtpOptions { Host = "smtp.example.com" }, SmtpEmailSender.FormatSender("no-reply@example.com", "Game"));
        using var mime = sender.CreateMessage(new EmailMessage("player@gmail.com", "Verify your email", "<p>Hi <b>player</b></p>", "Hi player"));

        var from = Assert.Single(mime.From.Mailboxes);
        Assert.Equal("Game", from.Name);
        Assert.Equal("no-reply@example.com", from.Address);
        Assert.Equal("player@gmail.com", Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal("Verify your email", mime.Subject);
        Assert.False(string.IsNullOrEmpty(mime.MessageId));

        var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal(2, alternative.Count);
        Assert.Equal("text/plain", alternative[0].ContentType.MimeType);
        Assert.Equal("text/html", alternative[1].ContentType.MimeType);
        Assert.Equal("Hi player", mime.TextBody);
        Assert.Equal("<p>Hi <b>player</b></p>", mime.HtmlBody);

        using var textOnly = sender.CreateMessage(new EmailMessage("a@b.io", "S", "", "only text"));
        Assert.IsType<TextPart>(textOnly.Body);
        Assert.Throws<ParseException>(() => sender.CreateMessage(new EmailMessage("", "S", "h", "t")));
    }

    [Fact]
    public async Task Sends_through_an_smtp_server_with_authentication()
    {
        await using var server = FakeSmtpServer.Start();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var sender = Sender(server, loggerFactory: loggerFactory);

        await sender.SendAsync(new EmailMessage("player@gmail.com", "Verify your email", "<p>Hi</p>", "Hi player\r\n.leading dot"));

        var session = Assert.Single(server.Sessions);
        Assert.Equal("\0mailer\0s3cret-pass", session.AuthPlain);
        Assert.Equal("<no-reply@example.com>", session.MailFrom);
        Assert.Equal("<player@gmail.com>", Assert.Single(session.RcptTo));
        Assert.True(session.Quit);

        using var received = MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(session.Data!)));
        Assert.Equal("Verify your email", received.Subject);
        Assert.Equal("Game", Assert.Single(received.From.Mailboxes).Name);
        Assert.Equal("Hi player\n.leading dot", received.TextBody.Replace("\r\n", "\n").TrimEnd('\n')); // dot-stuffing round-trips
        Assert.Equal("<p>Hi</p>", received.HtmlBody.TrimEnd('\r', '\n'));

        var logged = string.Join("\n", logs.Lines);
        Assert.Contains("gmail.com", logged);
        Assert.DoesNotContain("player@", logged);
        Assert.DoesNotContain("s3cret-pass", logged);
        Assert.DoesNotContain("Hi player", logged);
    }

    [Fact]
    public async Task Without_a_username_no_authentication_is_attempted()
    {
        await using var server = FakeSmtpServer.Start();
        await Sender(server, username: null).SendAsync(Message());
        var session = Assert.Single(server.Sessions);
        Assert.Null(session.AuthPlain);
        Assert.NotNull(session.Data);
    }

    [Fact]
    public async Task Rejected_credentials_throw_without_the_password()
    {
        await using var server = FakeSmtpServer.Start(new() { RejectAuth = true });
        var ex = await Assert.ThrowsAsync<AuthenticationException>(() => Sender(server).SendAsync(Message()));
        Assert.DoesNotContain("s3cret-pass", ex.Message);
        Assert.Null(Assert.Single(server.Sessions).Data);
    }

    [Fact]
    public async Task Rejected_recipient_throws_a_command_exception()
    {
        await using var server = FakeSmtpServer.Start(new() { RejectRecipient = true });
        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => Sender(server).SendAsync(Message("nobody@b.io")));
        Assert.Equal(SmtpErrorCode.RecipientNotAccepted, ex.ErrorCode);
        Assert.Null(Assert.Single(server.Sessions).Data);
    }

    [Fact]
    public async Task Connection_dropped_on_quit_after_acceptance_still_succeeds()
    {
        await using var server = FakeSmtpServer.Start(new() { DropOnQuit = true });
        await Sender(server).SendAsync(Message());
        Assert.NotNull(Assert.Single(server.Sessions).Data);
    }

    [Fact]
    public async Task Starttls_is_mandatory_and_never_downgraded()
    {
        await using var server = FakeSmtpServer.Start(); // does not offer STARTTLS
        await Assert.ThrowsAsync<NotSupportedException>(() => Sender(server, SmtpSecurity.StartTls).SendAsync(Message()));
        Assert.Null(Assert.Single(server.Sessions).AuthPlain); // credentials never sent in clear text
    }

    [Fact]
    public async Task Starttls_upgrades_before_authenticating_and_validates_the_certificate()
    {
        using var certificate = SelfSignedCertificate();
        await using var server = FakeSmtpServer.Start(new() { StartTlsCertificate = certificate });
        // The fake server's self-signed certificate is untrusted: the upgrade is attempted and the handshake rejected.
        await Assert.ThrowsAsync<SslHandshakeException>(() => Sender(server, SmtpSecurity.StartTls).SendAsync(Message()));
        var session = Assert.Single(server.Sessions);
        Assert.True(session.TlsHandshakeStarted);
        Assert.Null(session.AuthPlain);
        Assert.Equal(["EHLO", "STARTTLS"], session.Commands);
    }

    [Fact]
    public async Task Ssl_speaks_tls_from_the_first_byte()
    {
        using var certificate = SelfSignedCertificate();
        await using var server = FakeSmtpServer.Start(new() { ImplicitTlsCertificate = certificate });
        await Assert.ThrowsAsync<SslHandshakeException>(() => Sender(server, SmtpSecurity.Ssl).SendAsync(Message()));
        var session = Assert.Single(server.Sessions);
        Assert.True(session.TlsHandshakeStarted);
        Assert.Empty(session.Commands); // nothing went over the wire in clear text
    }

    [Fact]
    public async Task A_silent_server_times_out()
    {
        await using var server = FakeSmtpServer.Start(new() { Silent = true });
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => Sender(server, timeoutSeconds: 1).SendAsync(Message()));
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_aborts_the_send()
    {
        await using var server = FakeSmtpServer.Start(new() { Silent = true });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender(server, timeoutSeconds: 30).SendAsync(Message(), cts.Token));
    }

    private static X509Certificate2 SelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // SChannel (Windows) cannot serve TLS with an ephemeral key; a PKCS#12 round-trip gives it a persisted one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines) owner.Lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed record FakeSmtpBehavior
    {
        public bool RejectAuth { get; init; }
        public bool RejectRecipient { get; init; }
        public bool DropOnQuit { get; init; }
        /// <summary>Accepts connections but never sends the greeting.</summary>
        public bool Silent { get; init; }
        /// <summary>Offers STARTTLS and serves TLS with this certificate after it.</summary>
        public X509Certificate2? StartTlsCertificate { get; init; }
        /// <summary>Serves TLS from the first byte with this certificate.</summary>
        public X509Certificate2? ImplicitTlsCertificate { get; init; }
    }

    /// <summary>Just enough of an SMTP server (RFC 5321, AUTH PLAIN, STARTTLS) on loopback to receive one message per connection.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        public sealed class Session
        {
            public List<string> Commands { get; } = new();
            public bool TlsHandshakeStarted;
            public string? AuthPlain;
            public string? MailFrom;
            public List<string> RcptTo { get; } = new();
            public string? Data;
            public bool Quit;
        }

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly FakeSmtpBehavior _behavior;
        private readonly List<Session> _sessions = new();
        private readonly Task _loop;

        private FakeSmtpServer(FakeSmtpBehavior behavior)
        {
            _behavior = behavior;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }

        public static FakeSmtpServer Start(FakeSmtpBehavior? behavior = null) => new(behavior ?? new FakeSmtpBehavior());

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<Session> Sessions { get { lock (_sessions) return _sessions.ToList(); } }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                var session = new Session();
                lock (_sessions) _sessions.Add(session);
                try { await Serve(client, session); }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client went away or rejected the TLS handshake; the session records how far it got.
                }
                finally { client.Dispose(); }
            }
        }

        private async Task Serve(TcpClient client, Session session)
        {
            Stream stream = client.GetStream();
            if (_behavior.Silent)
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
                return;
            }
            if (_behavior.ImplicitTlsCertificate is { } implicitCertificate)
                stream = await StartTls(stream, implicitCertificate, session);
            await Converse(stream, session);
        }

        private async Task<Stream> StartTls(Stream stream, X509Certificate2 certificate, Session session)
        {
            session.TlsHandshakeStarted = true;
            var tls = new SslStream(stream);
            await tls.AuthenticateAsServerAsync(certificate);
            return tls;
        }

        private async Task Converse(Stream stream, Session session)
        {
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.local ESMTP ready");
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                session.Commands.Add(verb);
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.local");
                        if (_behavior.StartTlsCertificate is not null)
                            await writer.WriteLineAsync("250-STARTTLS");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 fake.local");
                        break;
                    case "STARTTLS":
                        await writer.WriteLineAsync("220 2.0.0 Ready to start TLS");
                        // The client sent nothing after STARTTLS, so the reader holds no buffered bytes.
                        var tls = await StartTls(stream, _behavior.StartTlsCertificate!, session);
                        await Converse(tls, session);
                        return;
                    case "AUTH":
                        var parts = line.Split(' ');
                        var payload = parts.Length > 2 ? parts[2] : null;
                        if (payload is null)
                        {
                            await writer.WriteLineAsync("334 ");
                            payload = await reader.ReadLineAsync(_cts.Token);
                        }
                        session.AuthPlain = Encoding.UTF8.GetString(Convert.FromBase64String(payload!));
                        await writer.WriteLineAsync(_behavior.RejectAuth ? "535 5.7.8 Authentication credentials invalid" : "235 2.7.0 Authentication successful");
                        break;
                    case "MAIL":
                        session.MailFrom = line[(line.IndexOf(':') + 1)..].Trim().Split(' ')[0];
                        await writer.WriteLineAsync("250 2.1.0 OK");
                        break;
                    case "RCPT":
                        if (_behavior.RejectRecipient)
                        {
                            await writer.WriteLineAsync("550 5.1.1 No such user");
                            break;
                        }
                        session.RcptTo.Add(line[(line.IndexOf(':') + 1)..].Trim().Split(' ')[0]);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_cts.Token) is { } dataLine && dataLine != ".")
                            data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                        session.Data = data.ToString();
                        await writer.WriteLineAsync("250 2.0.0 Queued");
                        break;
                    case "RSET":
                    case "NOOP":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "QUIT":
                        session.Quit = true;
                        if (!_behavior.DropOnQuit)
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("502 5.5.2 Command not recognized");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _loop;
            _cts.Dispose();
        }
    }
}
