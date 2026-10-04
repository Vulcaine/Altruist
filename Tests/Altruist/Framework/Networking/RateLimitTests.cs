/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Codec.MessagePack;

using MessagePack;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// The config-driven rate limit (0.9.9): token buckets per connection and bucket, gate → bucket
/// mapping from config or [RateLimit], payload cap, strike window and disconnect, the interceptor,
/// metrics, DI installation behind <c>rate-limit:enabled</c> and forgetting closed connections.
/// </summary>
public sealed class RateLimitTests
{
    [MessagePackObject]
    public sealed class Ping : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 601;
    }

    private sealed class AttributedPortal
    {
        [RateLimit("slow")]
        public Task OnSlow(Ping packet, string clientId) => Task.CompletedTask;
    }

    private sealed class Metrics : IRateLimitMetrics
    {
        public readonly List<RateVerdict> Verdicts = new();
        public void Checked(InterceptContext context, RateVerdict verdict)
        {
            lock (Verdicts)
                Verdicts.Add(verdict);
        }
    }

    /// <summary>The limits a typical action game uses: fast input, slow chat, a few heavy requests, the rest.</summary>
    private static IConfiguration GameConfig(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["altruist:server:transport:rate-limit:enabled"] = "true",
            ["altruist:server:transport:rate-limit:routes:0"] = "/game",
            ["altruist:server:transport:rate-limit:buckets:input:capacity"] = "300",
            ["altruist:server:transport:rate-limit:buckets:input:per-second"] = "90",
            ["altruist:server:transport:rate-limit:buckets:input:gates:0"] = "input",
            ["altruist:server:transport:rate-limit:buckets:chat:capacity"] = "3",
            ["altruist:server:transport:rate-limit:buckets:chat:per-second"] = "0.6",
            ["altruist:server:transport:rate-limit:buckets:chat:gates:0"] = "chat",
            ["altruist:server:transport:rate-limit:buckets:heavy:capacity"] = "4",
            ["altruist:server:transport:rate-limit:buckets:heavy:per-second"] = "1",
            ["altruist:server:transport:rate-limit:buckets:heavy:gates:0"] = "queue",
            ["altruist:server:transport:rate-limit:buckets:heavy:gates:1"] = "profile",
            ["altruist:server:transport:rate-limit:buckets:default:capacity"] = "20",
            ["altruist:server:transport:rate-limit:buckets:default:per-second"] = "20",
        };
        foreach (var (k, v) in extra ?? new())
            values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static (TokenBucketRateLimiter Limiter, Func<double, double> Advance) Limiter(IConfiguration? cfg = null)
    {
        var now = 0.0;
        var limiter = new TokenBucketRateLimiter(RateLimitOptions.FromConfiguration(cfg ?? GameConfig()), () => now);
        return (limiter, dt => now += dt);
    }

    [Fact]
    public void Options_are_read_from_kebab_case_keys_with_defaults()
    {
        var o = RateLimitOptions.FromConfiguration(GameConfig());
        Assert.True(o.Enabled);
        Assert.Equal(65536, o.MaxPayloadBytes);
        Assert.Equal(20, o.OversizeStrikes);
        Assert.Equal(60, o.StrikesToDisconnect);
        Assert.Equal(10, o.StrikeWindowSeconds);
        Assert.Equal("default", o.DefaultBucket);
        Assert.Equal(new[] { "/game" }, o.Routes);
        Assert.Equal(new[] { "queue", "profile" }, o.Buckets["heavy"].Gates);
        Assert.Equal(0.6, o.Buckets["chat"].PerSecond);

        var empty = RateLimitOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.False(empty.Enabled);
        Assert.Empty(empty.Buckets);
    }

    [Fact]
    public void Buckets_cap_each_gate_group_and_refill_over_time()
    {
        var (limiter, advance) = Limiter();
        // Input: a 300 burst, then 90/s.
        Assert.Equal(300, Enumerable.Range(0, 340).Count(_ => limiter.Check("c", "input", 20) == RateVerdict.Allow));
        Assert.Equal(20, Enumerable.Range(0, 30).Count(_ => limiter.Check("c", "ping", 20) == RateVerdict.Allow));
        Assert.Equal(3, Enumerable.Range(0, 5).Count(_ => limiter.Check("c", "chat", 5) == RateVerdict.Allow));
        advance(5);
        Assert.Equal(3, Enumerable.Range(0, 5).Count(_ => limiter.Check("c", "chat", 5) == RateVerdict.Allow));

        // Gates listed in one bucket share it; other buckets are unaffected.
        var (heavy, later) = Limiter();
        Assert.All(Enumerable.Range(0, 4), _ => Assert.Equal(RateVerdict.Allow, heavy.Check("c", "queue", 8)));
        Assert.Equal(RateVerdict.Drop, heavy.Check("c", "queue", 8));
        Assert.Equal(RateVerdict.Drop, heavy.Check("c", "profile", 8));
        Assert.Equal(RateVerdict.Allow, heavy.Check("c", "ping", 8));
        later(1.01);
        Assert.Equal(RateVerdict.Allow, heavy.Check("c", "profile", 8));

        // A steady rate under the refill never trips the limit; connections are independent.
        var (steady, tick) = Limiter();
        for (var i = 0; i < 600; i++)
        {
            tick(1 / 60.0);
            Assert.Equal(RateVerdict.Allow, steady.Check("d", "input", 20));
        }
        Assert.Equal(RateVerdict.Allow, steady.Check("other", "input", 20));
    }

    [Fact]
    public void Oversize_payloads_and_repeated_violations_close_the_connection_until_forgotten()
    {
        var (limiter, advance) = Limiter();
        Assert.Equal(RateVerdict.Drop, limiter.Check("c", "ping", 65537));
        // Oversize weighs 20 strikes: two more reach 60.
        Assert.Equal(RateVerdict.Drop, limiter.Check("c", "ping", 65537));
        Assert.Equal(RateVerdict.Disconnect, limiter.Check("c", "ping", 65537));
        Assert.Equal(RateVerdict.Drop, limiter.Check("c", "ping", 10));
        limiter.Forget("c");
        Assert.Equal(RateVerdict.Allow, limiter.Check("c", "ping", 10));

        var verdicts = Enumerable.Range(0, 200).Select(_ => limiter.Check("d", "join", 10)).ToList();
        Assert.Contains(RateVerdict.Disconnect, verdicts);
        Assert.Equal(20 + 59, verdicts.IndexOf(RateVerdict.Disconnect));

        // Strikes outside the window start over.
        var (windowed, later) = Limiter();
        for (var i = 0; i < 20 + 59; i++)
            windowed.Check("e", "join", 10);
        later(10.5);
        Assert.Equal(RateVerdict.Allow, windowed.Check("e", "join", 10));
        for (var i = 0; i < 19; i++)
            Assert.NotEqual(RateVerdict.Disconnect, windowed.Check("e", "join", 10));
    }

    [Fact]
    public void Strikes_to_disconnect_zero_only_drops()
    {
        var (limiter, _) = Limiter(GameConfig(new() { ["altruist:server:transport:rate-limit:strikes-to-disconnect"] = "0" }));
        Assert.DoesNotContain(RateVerdict.Disconnect, Enumerable.Range(0, 500).Select(_ => limiter.Check("c", "join", 10)));
    }

    [Fact]
    public void A_gate_without_a_configured_bucket_uses_its_attribute_then_the_default_bucket()
    {
        var slowGate = "test-slow-" + Guid.NewGuid().ToString("N");
        var plainGate = "test-plain-" + Guid.NewGuid().ToString("N");
        var portal = new AttributedPortal();
        PortalGateRegistry<IPortal>.Register(slowGate, new Func<Ping, string, Task>(portal.OnSlow));
        PortalGateRegistry<IPortal>.Register(plainGate, new Func<Ping, string, Task>((_, _) => Task.CompletedTask));
        var (limiter, _) = Limiter(GameConfig(new()
        {
            ["altruist:server:transport:rate-limit:buckets:slow:capacity"] = "2",
            ["altruist:server:transport:rate-limit:buckets:slow:per-second"] = "1",
        }));

        Assert.Equal("slow", limiter.BucketOf(slowGate));
        Assert.Equal("default", limiter.BucketOf(plainGate));
        Assert.Equal("default", limiter.BucketOf("no-such-gate"));
        Assert.Equal("input", limiter.BucketOf("input"));
        Assert.Equal(2, Enumerable.Range(0, 5).Count(_ => limiter.Check("c", slowGate, 4) == RateVerdict.Allow));

        // No default bucket configured: unlisted gates are unlimited (the payload cap still applies).
        var (open, _) = Limiter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:server:transport:rate-limit:buckets:input:capacity"] = "1",
            ["altruist:server:transport:rate-limit:buckets:input:per-second"] = "1",
            ["altruist:server:transport:rate-limit:buckets:input:gates:0"] = "input",
        }).Build());
        Assert.All(Enumerable.Range(0, 100), _ => Assert.Equal(RateVerdict.Allow, open.Check("c", "ping", 4)));
        Assert.Equal(RateVerdict.Drop, open.Check("c", "ping", 70_000));
    }

    [Fact]
    public async Task The_interceptor_rejects_over_the_limit_closes_abusers_and_reports_every_verdict()
    {
        var now = 0.0;
        var options = RateLimitOptions.FromConfiguration(GameConfig());
        var closed = new List<string>();
        var metrics = new Metrics();
        var interceptor = RateLimitInterceptor.Create(options, new TokenBucketRateLimiter(options, () => now),
            id => { closed.Add(id); return Task.CompletedTask; }, metrics: metrics);
        var rejected = 0;
        for (var i = 0; i < 200 && closed.Count == 0; i++)
        {
            // Packets for unknown events are intercepted with a null payload.
            var ctx = new InterceptContext("no-such-gate", "c1", 4, "/game");
            await interceptor.Intercept(ctx, null!);
            if (ctx.Rejected)
                rejected++;
        }

        Assert.Equal(new[] { "c1" }, closed);
        Assert.Equal(60, rejected);
        Assert.Equal(20 + 60, metrics.Verdicts.Count);
        Assert.Equal(20, metrics.Verdicts.Count(v => v == RateVerdict.Allow));
        Assert.Equal(RateVerdict.Disconnect, metrics.Verdicts[^1]);

        // Other routes and anonymous packets are not limited; Forget resets a connection.
        var other = new InterceptContext("no-such-gate", "c1", 4, "/admin/");
        await interceptor.Intercept(other, null!);
        Assert.False(other.Rejected);
        var anonymous = new InterceptContext("no-such-gate", "", 4, "/game");
        await interceptor.Intercept(anonymous, null!);
        Assert.False(anonymous.Rejected);
        interceptor.Forget("c1");
        var again = new InterceptContext("ping", "c1", 4, "/game/");
        await interceptor.Intercept(again, null!);
        Assert.False(again.Rejected);
    }

    [Fact]
    public async Task Installed_from_DI_only_when_enabled_and_closed_connections_are_forgotten()
    {
        static ServiceProvider Build(IConfiguration cfg)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.ClearProviders());
            services.AddSingleton(cfg);
            services.AddSingleton(new Mock<IConnectionStore>().Object);
            DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, new List<string>(), typeof(RateLimitInterceptor));
            return services.BuildServiceProvider();
        }

        using (var off = Build(GameConfig(new() { ["altruist:server:transport:rate-limit:enabled"] = "false" })))
            Assert.Empty(off.GetServices<IInterceptor>());
        using (var missing = Build(new ConfigurationBuilder().Build()))
            Assert.Empty(missing.GetServices<IInterceptor>());

        using var on = Build(GameConfig());
        var interceptor = Assert.IsType<RateLimitInterceptor>(Assert.Single(on.GetServices<IInterceptor>()));
        Assert.IsType<TokenBucketRateLimiter>(interceptor.Limiter);
        Assert.Equal(300, interceptor.Options.Buckets["input"].Capacity);

        var codecs = new Mock<ICodecResolver>();
        codecs.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(new MessagePackCodec());
        var manager = new ConnectionManager(new Mock<ISocketManager>().Object, codecs.Object, NullLoggerFactory.Instance,
            interceptors: on.GetServices<IInterceptor>());
        for (var i = 0; i < 25; i++)
            await interceptor.Intercept(new InterceptContext("ping", "c9", 4, "/game"), null!);
        var limited = new InterceptContext("ping", "c9", 4, "/game");
        await interceptor.Intercept(limited, null!);
        Assert.True(limited.Rejected);

        await manager.DisconnectAsync("c9");

        var fresh = new InterceptContext("ping", "c9", 4, "/game");
        await interceptor.Intercept(fresh, null!);
        Assert.False(fresh.Rejected);
    }

    [Fact]
    public async Task A_replacement_limiter_from_DI_is_used()
    {
        var limiter = new Mock<IRateLimiter>();
        limiter.Setup(l => l.Check(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).Returns(RateVerdict.Drop);
        var cfg = GameConfig();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(cfg);
        services.AddSingleton(new Mock<IConnectionStore>().Object);
        services.AddSingleton(limiter.Object);
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, new List<string>(), typeof(RateLimitInterceptor));
        using var sp = services.BuildServiceProvider();

        var interceptor = Assert.IsType<RateLimitInterceptor>(Assert.Single(sp.GetServices<IInterceptor>()));
        var ctx = new InterceptContext("input", "c", 4, "/game");
        await interceptor.Intercept(ctx, null!);
        Assert.True(ctx.Rejected);
        Assert.Same(limiter.Object, interceptor.Limiter);
    }
}
