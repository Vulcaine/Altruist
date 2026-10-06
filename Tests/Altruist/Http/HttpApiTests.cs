/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Altruist;
using Altruist.Http;
using Altruist.Security;
using Altruist.Transport;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Tests.Altruist.Framework.Scaling;

namespace Tests.Altruist.Http;

/// <summary>Endpoints for <see cref="HttpApiHostTests"/> (also discovered by other test hosts; harmless).</summary>
[ApiController]
[Route("api/http-api-test")]
public sealed class HttpApiTestController : ControllerBase
{
    [HttpGet("ip")]
    public IActionResult Ip() => Ok(new { ip = HttpContext.ClientIp(), https = Request.IsHttps });

    [HttpGet("limited")]
    [RateLimit("tight")]
    public IActionResult Limited() => NoContent();

    [HttpGet("fail/{kind}")]
    public IActionResult Fail(string kind) => kind switch
    {
        "api" => throw new HttpApiException(409, "name_taken", "That name is taken.", "userName", 7),
        "plain" => throw new HttpApiException(404, "not_found", "Nothing here."),
        _ => throw new InvalidOperationException("internal detail that must not leak"),
    };

    [HttpPost("echo")]
    public IActionResult Echo([FromBody] EchoRequest? body) => Ok(body);
}

public sealed class EchoRequest
{
    [Required] public string? UserName { get; set; }
    public int Count { get; set; }
}

public sealed class HttpApiOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Everything_is_off_by_default()
    {
        var s = HttpApiSettings.FromConfiguration(Config(new()));
        Assert.Equal(HttpErrorFormat.None, s.Errors.Format);
        Assert.True(s.Hardening.ServerHeader);
        Assert.Null(s.Hardening.MaxBodyBytes);
        Assert.False(s.Hardening.ForwardedHeaders);
        Assert.False(s.Hardening.SecurityHeaders);
        Assert.Null(s.Hardening.ApiPath);
        Assert.Empty(s.RateLimit.Policies);
        Assert.Equal(SharedCounterStore.Memory, s.RateLimit.Store);

        var services = new ServiceCollection();
        HttpApiConfiguration.Register(services, s);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAuthChallengeWriter));
    }

    [Fact]
    public void Keys_are_read_from_config()
    {
        var s = HttpApiSettings.FromConfiguration(Config(new()
        {
            ["altruist:server:http:errors:format"] = "simple",
            ["altruist:server:http:errors:messages:rate-limited"] = "Slow down.",
            ["altruist:server:http:hardening:server-header"] = "false",
            ["altruist:server:http:hardening:max-body-bytes"] = "1048576",
            ["altruist:server:http:hardening:forwarded-headers:enabled"] = "true",
            ["altruist:server:http:hardening:forwarded-headers:forward-limit"] = "2",
            ["altruist:server:http:hardening:forwarded-headers:known-proxies:0"] = "10.0.0.5",
            ["altruist:server:http:hardening:security-headers:enabled"] = "true",
            ["altruist:server:http:hardening:security-headers:hsts-max-age-seconds"] = "31536000",
            ["altruist:server:http:hardening:api:path"] = "/api",
            ["altruist:server:http:hardening:api:content-security-policy"] = "default-src 'none'",
            ["altruist:server:http:hardening:api:no-store"] = "true",
            ["altruist:server:http:hardening:api:max-body-bytes"] = "16384",
            ["altruist:server:http:rate-limit:store"] = "backplane",
            ["altruist:server:http:rate-limit:policies:auth:permit"] = "10",
            ["altruist:server:http:rate-limit:policies:auth:window-seconds"] = "60",
            ["altruist:server:http:rate-limit:policies:mail:permit"] = "3",
            ["altruist:server:http:rate-limit:policies:mail:window-seconds"] = "3600",
            ["altruist:server:http:rate-limit:policies:mail:partition"] = "principal",
            ["altruist:server:http:rate-limit:policies:api:permit"] = "600",
            ["altruist:server:http:rate-limit:policies:api:window-seconds"] = "60",
            ["altruist:server:http:rate-limit:policies:api:path"] = "/api",
        }));
        Assert.Equal(HttpErrorFormat.Simple, s.Errors.Format);
        Assert.Equal("Slow down.", s.Errors.Message(HttpErrorCodes.RateLimited));
        Assert.Equal("That request was not accepted.", s.Errors.Message(HttpErrorCodes.InvalidRequest));
        Assert.False(s.Hardening.ServerHeader);
        Assert.Equal(1048576, s.Hardening.MaxBodyBytes);
        Assert.True(s.Hardening.ForwardedHeaders);
        Assert.Equal(2, s.Hardening.ForwardLimit);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), Assert.Single(s.Hardening.KnownProxies));
        Assert.True(s.Hardening.SecurityHeaders);
        Assert.Equal(31536000, s.Hardening.HstsMaxAgeSeconds);
        Assert.Equal("/api", s.Hardening.ApiPath);
        Assert.Equal(16384, s.Hardening.ApiMaxBodyBytes);
        Assert.Equal(SharedCounterStore.Backplane, s.RateLimit.Store);
        Assert.Equal(RateLimitPartition.Ip, s.RateLimit.Policies["auth"].Partition);
        Assert.Equal(TimeSpan.FromHours(1), s.RateLimit.Policies["mail"].Window);
        Assert.Equal(RateLimitPartition.Principal, s.RateLimit.Policies["mail"].Partition);
        Assert.Equal("api", Assert.Single(s.RateLimit.PathPolicies).Name);

        var services = new ServiceCollection();
        HttpApiConfiguration.Register(services, s);
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthChallengeWriter));
    }

    [Theory]
    [InlineData("altruist:server:http:errors:format", "xml")]
    [InlineData("altruist:server:http:rate-limit:store", "disk")]
    [InlineData("altruist:server:http:rate-limit:policies:x:permit", "0")]
    [InlineData("altruist:server:http:rate-limit:policies:y:partition", "user")]
    [InlineData("altruist:server:http:hardening:forwarded-headers:known-proxies:0", "proxy.local")]
    public void Bad_values_are_refused(string key, string value) =>
        Assert.Throws<ArgumentException>(() => HttpApiSettings.FromConfiguration(Config(new()
        {
            ["altruist:server:http:rate-limit:policies:y:permit"] = "1",
            ["altruist:server:http:rate-limit:policies:y:window-seconds"] = "1",
            ["altruist:server:http:rate-limit:policies:x:permit"] = "1",
            ["altruist:server:http:rate-limit:policies:x:window-seconds"] = "1",
            [key] = value,
        })));

    [Fact]
    public void Without_the_simple_format_errors_are_bare_status_codes()
    {
        var none = new HttpErrorWriter(new HttpErrorOptions());
        Assert.Equal(429, Assert.IsType<StatusCodeResult>(none.Result(429, HttpErrorCodes.RateLimited)).StatusCode);
        var simple = new HttpErrorWriter(new HttpErrorOptions { Format = HttpErrorFormat.Simple });
        var result = Assert.IsType<ObjectResult>(simple.Result(429, HttpErrorCodes.RateLimited));
        Assert.Equal(new HttpError("rate_limited", "Too many requests. Please wait a moment."), result.Value);
    }
}

public sealed class RequestRateLimiterTests
{
    private static HttpRateLimitOptions Options(SharedCounterStore store = SharedCounterStore.Memory) => new HttpRateLimitOptions { Store = store }
        .Add(new HttpRateLimitPolicy { Name = "auth", Permit = 10, Window = TimeSpan.FromMinutes(1) })
        .Add(new HttpRateLimitPolicy { Name = "mail", Permit = 3, Window = TimeSpan.FromHours(1), Partition = RateLimitPartition.Principal });

    [Fact]
    public async Task Memory_windows_slide_and_report_retry_after()
    {
        var clock = new ManualUtc();
        var limiter = new RequestRateLimiter(Options(), clock: () => clock.Now);
        for (var i = 0; i < 10; i++)
            Assert.True((await limiter.HitAsync("auth", "1.2.3.4")).Allowed);
        var rejected = await limiter.HitAsync("auth", "1.2.3.4");
        Assert.False(rejected.Allowed);
        Assert.InRange(rejected.RetryAfterSeconds, 59, 60);
        Assert.True((await limiter.HitAsync("auth", "5.6.7.8")).Allowed);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await limiter.HitAsync("auth", "1.2.3.4")).Allowed);
    }

    [Fact]
    public async Task Rejected_hits_are_not_counted_and_the_window_slides_hit_by_hit()
    {
        var clock = new ManualUtc();
        var limiter = new RequestRateLimiter(Options(), clock: () => clock.Now);
        for (var i = 0; i < 3; i++)
        {
            Assert.True((await limiter.HitAsync("mail", "acc")).Allowed);
            clock.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.Equal(1800, (await limiter.HitAsync("mail", "acc")).RetryAfterSeconds);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True((await limiter.HitAsync("mail", "acc")).Allowed); // the first hit left the window
        Assert.False((await limiter.HitAsync("mail", "acc")).Allowed);
    }

    [Fact]
    public async Task Unknown_policies_do_not_limit()
    {
        var limiter = new RequestRateLimiter(Options());
        for (var i = 0; i < 50; i++)
            Assert.True((await limiter.HitAsync("nope", "x")).Allowed);
    }

    [Fact]
    public async Task Partitions_come_from_the_request()
    {
        var limiter = new RequestRateLimiter(Options());
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
        var signedIn = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "acc-1") }, "test")) };
        signedIn.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");

        for (var i = 0; i < 3; i++)
            Assert.True((await limiter.HitAsync("mail", signedIn)).Allowed);
        Assert.False((await limiter.HitAsync("mail", signedIn)).Allowed);
        Assert.False((await limiter.HitAsync("mail", "acc-1")).Allowed);
        // Anonymous requests of a principal policy count per address.
        Assert.True((await limiter.HitAsync("mail", anonymous)).Allowed);
        // An ip policy counts the address whoever is signed in.
        for (var i = 0; i < 10; i++)
            Assert.True((await limiter.HitAsync("auth", i % 2 == 0 ? signedIn : anonymous)).Allowed);
        Assert.False((await limiter.HitAsync("auth", anonymous)).Allowed);
    }

    [Fact]
    public async Task A_fleet_on_a_shared_backplane_enforces_one_limit()
    {
        var clock = new ManualUtc { Now = new DateTime(2026, 10, 6, 12, 0, 10, DateTimeKind.Utc) };
        var backplane = new InMemoryFleetBackplane(shared: true, () => clock.Now);
        var servers = Enumerable.Range(0, 3).Select(_ => new RequestRateLimiter(Options(SharedCounterStore.Backplane), backplane, clock: () => clock.Now)).ToArray();
        for (var i = 0; i < 10; i++)
            Assert.True((await servers[i % 3].HitAsync("auth", "1.2.3.4")).Allowed);
        var rejected = await servers[1].HitAsync("auth", "1.2.3.4");
        Assert.False(rejected.Allowed);
        Assert.Equal(50, rejected.RetryAfterSeconds); // fixed window: the minute ends at 12:01:00
        Assert.True((await servers[2].HitAsync("auth", "9.9.9.9")).Allowed);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True((await servers[0].HitAsync("auth", "1.2.3.4")).Allowed);
    }

    [Fact]
    public async Task Memory_store_and_private_backplanes_count_per_server()
    {
        var shared = new InMemoryFleetBackplane(shared: true);
        var a = new RequestRateLimiter(Options(SharedCounterStore.Memory), shared);
        var b = new RequestRateLimiter(Options(SharedCounterStore.Memory), shared);
        for (var i = 0; i < 10; i++) Assert.True((await a.HitAsync("auth", "ip")).Allowed);
        Assert.True((await b.HitAsync("auth", "ip")).Allowed);

        // store: backplane with the process-local default backplane is memory (exact sliding windows).
        var local = new RequestRateLimiter(Options(SharedCounterStore.Backplane), new InMemoryFleetBackplane());
        for (var i = 0; i < 10; i++) Assert.True((await local.HitAsync("auth", "ip")).Allowed);
        Assert.False((await local.HitAsync("auth", "ip")).Allowed);
    }

    [Fact]
    public async Task A_backplane_without_counters_keeps_them_in_memory()
    {
        IFleetBackplane old = new CounterlessBackplane(new InMemoryFleetBackplane(shared: true));
        await Assert.ThrowsAsync<NotSupportedException>(() => old.IncrementAsync("k", TimeSpan.FromSeconds(1)));
        var limiter = new RequestRateLimiter(Options(SharedCounterStore.Backplane), old);
        for (var i = 0; i < 10; i++)
            Assert.True((await limiter.HitAsync("auth", "ip")).Allowed);
        Assert.False((await limiter.HitAsync("auth", "ip")).Allowed);
    }

    [Fact]
    public async Task An_unreachable_backplane_falls_back_to_memory_and_is_retried()
    {
        var clock = new ManualUtc();
        var broken = new BrokenBackplane(new InMemoryFleetBackplane(shared: true, () => clock.Now)) { Down = true };
        var limiter = new RequestRateLimiter(Options(SharedCounterStore.Backplane), broken, clock: () => clock.Now);
        for (var i = 0; i < 10; i++)
            Assert.True((await limiter.HitAsync("auth", "ip")).Allowed);
        Assert.False((await limiter.HitAsync("auth", "ip")).Allowed);
        Assert.Equal(1, broken.Calls); // one failure, then memory until the retry delay is over

        broken.Down = false;
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await limiter.HitAsync("auth", "ip")).Allowed);
        Assert.Equal(2, broken.Calls);
    }
}

/// <summary>A backplane written before shared counters existed (no IncrementAsync).</summary>
internal sealed class CounterlessBackplane(IFleetBackplane inner) : IFleetBackplane
{
    public string Kind => "old";
    public bool Shared => true;
    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) => inner.SetAsync(key, value, ttl, ct);
    public Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken ct = default) => inner.SetManyAsync(values, ttl, ct);
    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) => inner.SetIfAbsentAsync(key, value, ttl, ct);
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
    public Task<string?> TakeAsync(string key, CancellationToken ct = default) => inner.TakeAsync(key, ct);
    public Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken ct = default) => inner.GetByPrefixAsync(prefix, ct);
    public Task DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
    public Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken ct = default) => inner.DeleteIfValueAsync(key, value, ct);
}

/// <summary>A backplane that throws while <see cref="Down"/>.</summary>
internal sealed class BrokenBackplane(IFleetBackplane inner) : IFleetBackplane
{
    public volatile bool Down;
    public int Calls;
    public string Kind => "broken";
    public bool Shared => true;
    private Task Check() { Interlocked.Increment(ref Calls); return Down ? Task.FromException(new InvalidOperationException("backplane down")) : Task.CompletedTask; }
    public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Check(); await inner.SetAsync(key, value, ttl, ct); }
    public async Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken ct = default) { await Check(); await inner.SetManyAsync(values, ttl, ct); }
    public async Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Check(); return await inner.SetIfAbsentAsync(key, value, ttl, ct); }
    public async Task<string?> GetAsync(string key, CancellationToken ct = default) { await Check(); return await inner.GetAsync(key, ct); }
    public async Task<string?> TakeAsync(string key, CancellationToken ct = default) { await Check(); return await inner.TakeAsync(key, ct); }
    public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken ct = default) { await Check(); return await inner.GetByPrefixAsync(prefix, ct); }
    public async Task DeleteAsync(string key, CancellationToken ct = default) { await Check(); await inner.DeleteAsync(key, ct); }
    public async Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken ct = default) { await Check(); return await inner.DeleteIfValueAsync(key, value, ct); }
    public async Task<long> IncrementAsync(string key, TimeSpan ttl, long by = 1, CancellationToken ct = default) { await Check(); return await inner.IncrementAsync(key, ttl, by, ct); }
}

/// <summary>
/// The HTTP API features on a real Kestrel host built by <see cref="AltruistStartupConfiguration"/>:
/// error bodies, hardening headers, body limits, forwarded headers and rate limits in their order.
/// </summary>
public sealed class HttpApiHostTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private ServiceProvider? _bootstrap;

    public async Task InitializeAsync()
    {
        var settings = HttpApiSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:server:http:errors:format"] = "simple",
            ["altruist:server:http:hardening:server-header"] = "false",
            ["altruist:server:http:hardening:max-body-bytes"] = "65536",
            ["altruist:server:http:hardening:forwarded-headers:enabled"] = "true",
            ["altruist:server:http:hardening:forwarded-headers:forward-limit"] = "2",
            ["altruist:server:http:hardening:security-headers:enabled"] = "true",
            ["altruist:server:http:hardening:security-headers:hsts-max-age-seconds"] = "31536000",
            ["altruist:server:http:hardening:api:path"] = "/api",
            ["altruist:server:http:hardening:api:content-security-policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'",
            ["altruist:server:http:hardening:api:no-store"] = "true",
            ["altruist:server:http:hardening:api:max-body-bytes"] = "1024",
            ["altruist:server:http:rate-limit:policies:tight:permit"] = "2",
            ["altruist:server:http:rate-limit:policies:tight:window-seconds"] = "60",
            ["altruist:server:http:rate-limit:policies:api:permit"] = "40",
            ["altruist:server:http:rate-limit:policies:api:window-seconds"] = "60",
            ["altruist:server:http:rate-limit:policies:api:path"] = "/api",
        }).Build());

        var root = new ServiceCollection();
        root.AddSingleton(new MutableConfigSource(new MutableConfigProvider()));
        HttpApiConfiguration.Register(root, settings);
        root.AddSingleton<IRequestRateLimiter>(new RequestRateLimiter(settings.RateLimit));
        var status = new Mock<IServerStatus>();
        status.Setup(s => s.Status).Returns(ReadyState.Alive);
        root.AddSingleton(status.Object);
        var context = new Mock<IAltruistContext>();
        context.SetupProperty(c => c.ServerInfo);
        context.Setup(c => c.Endpoints).Returns(new HashSet<string>());

        _bootstrap = root.BuildServiceProvider();
        var startup = new AltruistStartupConfiguration(
            new ApplicationArgs(), "127.0.0.1", "0", "/", "/ws",
            NullLoggerFactory.Instance, context.Object, status.Object, Array.Empty<ITransport>());
        _app = (await startup.BuildAndStartAsync(root, _bootstrap))!;
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (_bootstrap is not null) await _bootstrap.DisposeAsync();
    }

    private static string Client() => $"203.0.113.{Random.Shared.Next(1, 250)}";

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content = null, string? forwardedFor = null, string? proto = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add("X-Forwarded-For", forwardedFor ?? Client());
        if (proto is not null) req.Headers.Add("X-Forwarded-Proto", proto);
        return await _http.SendAsync(req);
    }

    private static async Task<string> Body(HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    [Fact]
    public async Task Api_exceptions_become_error_bodies_with_retry_after()
    {
        using var res = await Send(HttpMethod.Get, "/api/http-api-test/fail/api");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("{\"error\":\"name_taken\",\"message\":\"That name is taken.\",\"field\":\"userName\"}", await Body(res));
        Assert.Equal("7", res.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType!.ToString());

        using var plain = await Send(HttpMethod.Get, "/api/http-api-test/fail/plain");
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.Equal("{\"error\":\"not_found\",\"message\":\"Nothing here.\"}", await Body(plain));
        Assert.False(plain.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Unexpected_exceptions_are_500s_without_details()
    {
        using var res = await Send(HttpMethod.Get, "/api/http-api-test/fail/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("{\"error\":\"internal\",\"message\":\"The server had a problem. Please try again shortly.\"}", await Body(res));
    }

    [Fact]
    public async Task Invalid_models_name_the_first_invalid_field_in_camel_case()
    {
        using var missing = await Send(HttpMethod.Post, "/api/http-api-test/echo", new StringContent("{\"count\":1}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\",\"message\":\"That request was not accepted.\",\"field\":\"userName\"}", await Body(missing));

        // Body-level errors ($-paths, malformed JSON) name no field.
        using var malformed = await Send(HttpMethod.Post, "/api/http-api-test/echo", new StringContent("{\"userName\":", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\",\"message\":\"That request was not accepted.\"}", await Body(malformed));
    }

    [Fact]
    public async Task Security_headers_on_every_response_and_api_headers_under_the_api_path()
    {
        using var api = await Send(HttpMethod.Get, "/api/http-api-test/ip");
        Assert.Equal("nosniff", api.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", api.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", api.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("same-origin", api.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'", api.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
        Assert.False(api.Headers.Contains("Strict-Transport-Security")); // plain HTTP
        Assert.False(api.Headers.Contains("Server"));

        using var other = await Send(HttpMethod.Get, "/not-the-api");
        Assert.Equal("nosniff", other.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(other.Headers.Contains("Content-Security-Policy"));
        Assert.False(other.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Forwarded_headers_from_the_loopback_proxy_set_client_and_scheme()
    {
        using var res = await Send(HttpMethod.Get, "/api/http-api-test/ip", forwardedFor: "198.51.100.23", proto: "https");
        var body = JsonDocument.Parse(await Body(res)).RootElement;
        Assert.Equal("198.51.100.23", body.GetProperty("ip").GetString());
        Assert.True(body.GetProperty("https").GetBoolean());
        Assert.Equal("max-age=31536000", res.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Api_bodies_over_the_limit_are_413_with_and_without_a_length()
    {
        var big = "{\"userName\":\"" + new string('x', 2000) + "\"}";
        using var declared = await Send(HttpMethod.Post, "/api/http-api-test/echo", new StringContent(big, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal("{\"error\":\"payload_too_large\",\"message\":\"That request is too large.\"}", await Body(declared));

        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
        chunked.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/http-api-test/echo") { Content = chunked };
        req.Headers.TransferEncodingChunked = true;
        req.Headers.Add("X-Forwarded-For", Client());
        using var streamed = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        Assert.Equal("{\"error\":\"payload_too_large\",\"message\":\"That request is too large.\"}", await Body(streamed));

        using var small = await Send(HttpMethod.Post, "/api/http-api-test/echo", new StringContent("{\"userName\":\"x\",\"count\":2}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
    }

    [Fact]
    public async Task Rate_limited_actions_answer_429_with_retry_after_per_client()
    {
        var me = Client();
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Get, "/api/http-api-test/limited", forwardedFor: me)).StatusCode);
        using var limited = await Send(HttpMethod.Get, "/api/http-api-test/limited", forwardedFor: me);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("{\"error\":\"rate_limited\",\"message\":\"Too many requests. Please wait a moment.\"}", await Body(limited));
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 59, 60);
        Assert.Equal("no-store", limited.Headers.CacheControl!.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Get, "/api/http-api-test/limited", forwardedFor: "192.0.2.77")).StatusCode);
    }

    [Fact]
    public async Task Path_policies_limit_every_request_under_the_path_before_the_body_limit()
    {
        var me = "192.0.2.200";
        for (var i = 0; i < 40; i++)
            Assert.NotEqual((HttpStatusCode)429, (await Send(HttpMethod.Get, "/api/http-api-test/ip", forwardedFor: me)).StatusCode);
        var big = new StringContent(new string('x', 5000), Encoding.UTF8, "application/json");
        using var limited = await Send(HttpMethod.Post, "/api/http-api-test/echo", big, forwardedFor: me);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("{\"error\":\"rate_limited\",\"message\":\"Too many requests. Please wait a moment.\"}", await Body(limited));
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("nosniff", limited.Headers.GetValues("X-Content-Type-Options").Single());
        // Outside the path: not limited.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/not-the-api", forwardedFor: me)).StatusCode);
    }
}
