/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Text;

using Altruist;
using Altruist.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Tests.Altruist.Framework.Scaling;
using Tests.Altruist.Http;

namespace Tests.Altruist.Security;

public sealed class FailureLockoutTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Max_failures_within_the_window_lock_the_key()
    {
        var clock = new ManualUtc();
        var lockout = new FailureLockout(clock: () => clock.Now);
        for (var i = 0; i < 4; i++)
            Assert.False(await lockout.RecordFailureAsync("login:bob", 5, Window, LockFor));
        Assert.Equal(TimeSpan.Zero, await lockout.LockedForAsync("login:bob"));
        Assert.True(await lockout.RecordFailureAsync("login:bob", 5, Window, LockFor));
        Assert.Equal(LockFor, await lockout.LockedForAsync("login:bob"));
        Assert.Equal(TimeSpan.Zero, await lockout.LockedForAsync("login:alice"));

        clock.Advance(LockFor + TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, await lockout.LockedForAsync("login:bob"));
    }

    [Fact]
    public async Task Old_failures_leave_the_window_and_clear_forgets_them()
    {
        var clock = new ManualUtc();
        var lockout = new FailureLockout(clock: () => clock.Now);
        for (var i = 0; i < 4; i++)
            await lockout.RecordFailureAsync("k", 5, Window, LockFor);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(await lockout.RecordFailureAsync("k", 5, Window, LockFor));
        await lockout.ClearAsync("k");
        for (var i = 0; i < 4; i++)
            Assert.False(await lockout.RecordFailureAsync("k", 5, Window, LockFor));
    }

    [Fact]
    public async Task Servers_on_a_shared_backplane_see_one_count_and_one_lock()
    {
        var clock = new ManualUtc();
        var backplane = new InMemoryFleetBackplane(shared: true, () => clock.Now);
        var options = new FailureLockoutOptions { Store = SharedCounterStore.Backplane };
        var a = new FailureLockout(options, backplane, clock: () => clock.Now);
        var b = new FailureLockout(options, backplane, clock: () => clock.Now);
        for (var i = 0; i < 4; i++)
            Assert.False(await (i % 2 == 0 ? a : b).RecordFailureAsync("login:bob|203.0.113.9", 5, Window, LockFor));
        Assert.True(await b.RecordFailureAsync("login:bob|203.0.113.9", 5, Window, LockFor));
        Assert.Equal(LockFor, await a.LockedForAsync("login:bob|203.0.113.9"));
        // Keys are stored hashed.
        Assert.DoesNotContain((await backplane.GetByPrefixAsync("")).Keys, k => k.Contains("bob"));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(10), await b.LockedForAsync("login:bob|203.0.113.9"));
        await a.ClearAsync("login:bob|203.0.113.9");
        Assert.Equal(TimeSpan.Zero, await b.LockedForAsync("login:bob|203.0.113.9"));
    }

    [Fact]
    public async Task An_unreachable_backplane_falls_back_to_memory()
    {
        var broken = new BrokenBackplane(new InMemoryFleetBackplane(shared: true)) { Down = true };
        var lockout = new FailureLockout(new FailureLockoutOptions { Store = SharedCounterStore.Backplane }, broken);
        for (var i = 0; i < 2; i++)
            Assert.False(await lockout.RecordFailureAsync("k", 3, Window, LockFor));
        Assert.True(await lockout.RecordFailureAsync("k", 3, Window, LockFor));
        Assert.True(await lockout.LockedForAsync("k") > TimeSpan.Zero);
    }

    [Fact]
    public void Store_is_read_from_config()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["altruist:security:lockout:store"] = "backplane" }).Build();
        Assert.Equal(SharedCounterStore.Backplane, FailureLockoutOptions.FromConfiguration(cfg).Store);
        Assert.Equal(SharedCounterStore.Memory, FailureLockoutOptions.FromConfiguration(new ConfigurationBuilder().Build()).Store);
    }
}

public sealed class CaptchaTests
{
    private static TurnstileCaptchaVerifier Verifier(string? secret, HttpMessageHandler handler, string provider = "turnstile", string? siteKey = "site") =>
        new(CaptchaOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:security:captcha:provider"] = provider,
            ["altruist:security:captcha:site-key"] = siteKey,
            ["altruist:security:captcha:secret-key"] = secret,
        }).Build()), new HttpClient(handler), NullLoggerFactory.Instance);

    [Fact]
    public async Task Off_without_a_secret()
    {
        var off = Verifier(null, new StubHandler(HttpStatusCode.OK, "{\"success\":false}"));
        Assert.False(off.Enabled);
        Assert.Null(off.SiteKey);
        Assert.True(await off.VerifyAsync("", null));
    }

    [Fact]
    public async Task Posts_siteverify_and_accepts_only_success()
    {
        var ok = new StubHandler(HttpStatusCode.OK, "{\"success\":true}");
        var on = Verifier("1x0000000000000000000000000000000AA", ok);
        Assert.True(on.Enabled);
        Assert.Equal("site", on.SiteKey);
        Assert.True(await on.VerifyAsync("token-1", "198.51.100.4"));
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", ok.Request!.RequestUri!.ToString());
        Assert.Contains("secret=1x0000000000000000000000000000000AA", ok.Body);
        Assert.Contains("response=token-1", ok.Body);
        Assert.Contains("remoteip=198.51.100.4", ok.Body);

        await on.VerifyAsync("token-2", "unknown");
        Assert.DoesNotContain("remoteip", ok.Body);

        Assert.False(await Verifier("s", new StubHandler(HttpStatusCode.OK, "{\"success\":false,\"error-codes\":[\"invalid-input-response\"]}")).VerifyAsync("t", null));
    }

    [Fact]
    public async Task Fails_closed_and_rejects_empty_or_oversized_tokens_without_a_call()
    {
        Assert.False(await Verifier("s", new StubHandler(HttpStatusCode.BadGateway, "<html>")).VerifyAsync("t", null));
        Assert.False(await Verifier("s", new ThrowingHandler()).VerifyAsync("t", null));
        var counting = new StubHandler(HttpStatusCode.OK, "{\"success\":true}");
        var v = Verifier("s", counting);
        Assert.False(await v.VerifyAsync("", null));
        Assert.False(await v.VerifyAsync(new string('x', 2049), null));
        Assert.Null(counting.Request);
        Assert.True(await v.VerifyAsync(new string('x', 2048), null));
    }

    [Fact]
    public void Providers_pick_their_siteverify_endpoint()
    {
        Assert.Equal("https://api.hcaptcha.com/siteverify", Verifier("s", new ThrowingHandler(), "hcaptcha").Options.VerifyUrl);
        Assert.Equal("https://www.google.com/recaptcha/api/siteverify", Verifier("s", new ThrowingHandler(), "recaptcha").Options.VerifyUrl);
        Assert.Throws<ArgumentException>(() => Verifier("s", new ThrowingHandler(), "mystery"));
    }

    internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }
}
