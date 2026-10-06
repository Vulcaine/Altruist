/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;

using Altruist.Email;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Tests.Altruist.Security;

namespace Tests.Altruist.Email;

public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("racer@gmail.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("a@b.io", true)]
    [InlineData("no-at-sign.com", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("dot.@example.com", false)]
    [InlineData("user@localhost", false)]
    [InlineData("user@-bad.com", false)]
    [InlineData("spa ce@example.com", false)]
    [InlineData("line\n@example.com", false)]
    [InlineData("user@example.com\n", false)]
    [InlineData("Upper@Example.com", false)] // validates the normalized form
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Rules(string? email, bool valid) => Assert.Equal(valid, EmailAddress.IsValid(email));

    [Fact]
    public void Addresses_are_trimmed_lower_cased_and_capped()
    {
        Assert.Equal("racer@gmail.com", EmailAddress.Normalize("  Racer@GMail.com "));
        Assert.Null(EmailAddress.Normalize("   "));
        Assert.Null(EmailAddress.Normalize(null));
        var local = new string('a', 64);
        var domain = string.Join(".", Enumerable.Repeat(new string('b', 60), 3)) + ".com";
        Assert.True(EmailAddress.IsValid($"{local}@{domain}"));
        Assert.False(EmailAddress.IsValid($"{local}a@{domain}"));
        var longDomain = string.Join(".", Enumerable.Repeat(new string('c', 60), 4)) + ".com";
        Assert.False(EmailAddress.IsValid($"{local}@{longDomain}"));
        Assert.Equal(254, EmailAddress.MaxLength);
    }
}

public sealed class EmailSenderTests
{
    private static EmailOptions Options(Dictionary<string, string?> values) =>
        EmailOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Options_default_to_logging()
    {
        var o = Options(new());
        Assert.Equal("log", o.Provider);
        Assert.False(o.SendsRealEmail);
        var resend = Options(new() { ["altruist:email:provider"] = "Resend", ["altruist:email:from"] = "Game <no-reply@example.com>" });
        Assert.Equal("resend", resend.Provider);
        Assert.False(resend.SendsRealEmail); // no key
        Assert.True(Options(new() { ["altruist:email:provider"] = "resend", ["altruist:email:resend-api-key"] = "re_1" }).SendsRealEmail);
    }

    [Fact]
    public async Task Resend_posts_json_with_a_bearer_key_and_reports_failures()
    {
        var handler = new CaptchaTests.StubHandler(HttpStatusCode.OK, "{\"id\":\"1\"}");
        var sender = new ResendEmailSender("re_test", "Game <no-reply@example.com>", new HttpClient(handler));
        await sender.SendAsync(new EmailMessage("a@b.io", "Subject", "<p>hi</p>", "hi"));
        Assert.Equal(ResendEmailSender.Endpoint, handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer re_test", handler.Request.Headers.Authorization!.ToString());
        Assert.Contains("\"to\":[\"a@b.io\"]", handler.Body);
        Assert.Contains("\"from\":\"Game \\u003Cno-reply@example.com\\u003E\"", handler.Body);
        Assert.Contains("\"html\":\"\\u003Cp\\u003Ehi\\u003C/p\\u003E\"", handler.Body);
        Assert.Contains("\"text\":\"hi\"", handler.Body);

        var failing = new ResendEmailSender("re_test", "x@example.com", new HttpClient(new CaptchaTests.StubHandler(HttpStatusCode.Forbidden, "{\"message\":\"nope\"}")));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => failing.SendAsync(new EmailMessage("a@b.io", "S", "h", "t")));
        Assert.DoesNotContain("re_test", ex.Message);
    }

    [Fact]
    public async Task Without_a_provider_emails_are_logged_not_sent()
    {
        var sender = new ConfiguredEmailSender(Options(new() { ["altruist:email:provider"] = "resend" }), NullLoggerFactory.Instance);
        await sender.SendAsync(new EmailMessage("a@b.io", "S", "h", "t")); // LogEmailSender: no network
        Assert.False(sender.Options.SendsRealEmail);
    }

    [Fact]
    public async Task Background_sends_report_failures_instead_of_throwing()
    {
        Exception? seen = null;
        await new FailingSender().SendInBackground(new EmailMessage("a@b.io", "S", "h", "t"), ex => seen = ex);
        Assert.IsType<InvalidOperationException>(seen);

        await new FailingSender().SendInBackground(new EmailMessage("a@b.io", "S", "h", "t"), _ => throw new Exception("handler"));
        await new FailingSender().SendInBackground(new EmailMessage("a@b.io", "S", "h", "t"), NullLogger.Instance);
    }

    private sealed class FailingSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => throw new InvalidOperationException("provider down");
    }
}
