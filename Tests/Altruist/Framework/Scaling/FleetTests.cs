/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.Sockets;

using Altruist;
using Altruist.Redis;

using StackExchange.Redis;

namespace Tests.Altruist.Framework.Scaling;

/// <summary>A clock tests move by hand (node and claim lifetimes).</summary>
public sealed class ManualUtc
{
    public DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Runs only when a Redis is reachable (env <c>ALTRUIST_TEST_REDIS</c>, default 127.0.0.1:6379).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public static readonly string Address = Environment.GetEnvironmentVariable("ALTRUIST_TEST_REDIS") is { Length: > 0 } a ? a : "127.0.0.1:6379";

    private static readonly Lazy<bool> Reachable = new(() =>
    {
        try
        {
            var parts = Address.Split(':');
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(parts[0], int.Parse(parts[1])).Wait(TimeSpan.FromMilliseconds(500)) && tcp.Connected;
        }
        catch (Exception) { return false; }
    });

    public RedisFactAttribute()
    {
        if (!Reachable.Value) Skip = $"No Redis at {Address} (set ALTRUIST_TEST_REDIS).";
    }
}

/// <summary>The backplane contract, run against every implementation.</summary>
public abstract class FleetBackplaneContract
{
    protected abstract IFleetBackplane Create();

    /// <summary>Unique per test: Redis is shared between test runs.</summary>
    protected readonly string P = $"t{Guid.NewGuid():N}:";

    protected async Task Values_expire_and_can_be_read_listed_and_deleted()
    {
        var b = Create();
        await b.SetAsync(P + "a", "1", TimeSpan.FromSeconds(30));
        await b.SetManyAsync(new[] { KeyValuePair.Create(P + "b", "2"), KeyValuePair.Create(P + "c", "3") }, TimeSpan.FromSeconds(30));
        await b.SetAsync(P + "x:other", "9", TimeSpan.FromSeconds(30));
        Assert.Equal("1", await b.GetAsync(P + "a"));
        Assert.Null(await b.GetAsync(P + "missing"));
        var all = await b.GetByPrefixAsync(P);
        Assert.Equal(4, all.Count);
        Assert.Equal("2", all[P + "b"]);
        var some = await b.GetByPrefixAsync(P + "x:");
        Assert.Equal(new[] { P + "x:other" }, some.Keys);
        await b.DeleteAsync(P + "a");
        Assert.Null(await b.GetAsync(P + "a"));
    }

    protected async Task SetIfAbsent_wins_once()
    {
        var b = Create();
        Assert.True(await b.SetIfAbsentAsync(P + "k", "first", TimeSpan.FromSeconds(30)));
        Assert.False(await b.SetIfAbsentAsync(P + "k", "second", TimeSpan.FromSeconds(30)));
        Assert.Equal("first", await b.GetAsync(P + "k"));
    }

    protected async Task Take_returns_a_value_once()
    {
        var b = Create();
        await b.SetAsync(P + "h", "payload", TimeSpan.FromSeconds(30));
        Assert.Equal("payload", await b.TakeAsync(P + "h"));
        Assert.Null(await b.TakeAsync(P + "h"));
        Assert.Null(await b.GetAsync(P + "h"));
    }

    protected async Task DeleteIfValue_only_removes_your_own_value()
    {
        var b = Create();
        await b.SetAsync(P + "claim", "node-a", TimeSpan.FromSeconds(30));
        Assert.False(await b.DeleteIfValueAsync(P + "claim", "node-b"));
        Assert.Equal("node-a", await b.GetAsync(P + "claim"));
        Assert.True(await b.DeleteIfValueAsync(P + "claim", "node-a"));
        Assert.Null(await b.GetAsync(P + "claim"));
    }

    protected async Task Concurrent_claims_have_exactly_one_winner()
    {
        var b = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => b.SetIfAbsentAsync(P + "race", $"n{i}", TimeSpan.FromSeconds(30)))));
        Assert.Equal(1, results.Count(r => r));
    }
}

public class InMemoryFleetBackplaneTests : FleetBackplaneContract
{
    private readonly ManualUtc _clock = new();
    protected override IFleetBackplane Create() => new InMemoryFleetBackplane(shared: true, () => _clock.Now);

    [Fact] public Task Values() => Values_expire_and_can_be_read_listed_and_deleted();
    [Fact] public Task Claim_once() => SetIfAbsent_wins_once();
    [Fact] public Task Take_once() => Take_returns_a_value_once();
    [Fact] public Task Delete_own() => DeleteIfValue_only_removes_your_own_value();
    [Fact] public Task Race() => Concurrent_claims_have_exactly_one_winner();

    [Fact]
    public async Task Expired_values_are_gone_and_can_be_claimed_again()
    {
        var b = Create();
        await b.SetAsync("k", "v", TimeSpan.FromSeconds(5));
        Assert.True(await b.SetIfAbsentAsync("c", "a", TimeSpan.FromSeconds(5)));
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await b.GetAsync("k"));
        Assert.Empty(await b.GetByPrefixAsync(""));
        Assert.Null(await b.TakeAsync("k"));
        Assert.True(await b.SetIfAbsentAsync("c", "b", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_default_one_is_private_to_the_process()
    {
        var b = new InMemoryFleetBackplane();
        Assert.False(b.Shared);
        Assert.Equal("memory", b.Kind);
    }
}

public class RedisFleetBackplaneTests : FleetBackplaneContract, IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _mux = new(() => ConnectionMultiplexer.Connect(RedisFactAttribute.Address));
    protected override IFleetBackplane Create() => new RedisFleetBackplane(_mux.Value);

    [RedisFact] public Task Values() => Values_expire_and_can_be_read_listed_and_deleted();
    [RedisFact] public Task Claim_once() => SetIfAbsent_wins_once();
    [RedisFact] public Task Take_once() => Take_returns_a_value_once();
    [RedisFact] public Task Delete_own() => DeleteIfValue_only_removes_your_own_value();
    [RedisFact] public Task Race() => Concurrent_claims_have_exactly_one_winner();

    [RedisFact]
    public async Task Values_expire_in_redis()
    {
        var b = Create();
        await b.SetAsync(P + "short", "v", TimeSpan.FromSeconds(2));
        Assert.Equal("v", await b.GetAsync(P + "short"));
        await Task.Delay(2600);
        Assert.Null(await b.GetAsync(P + "short"));
    }

    [RedisFact]
    public async Task Prefix_listing_does_not_treat_glob_characters_as_patterns()
    {
        var b = Create();
        await b.SetAsync(P + "a*b", "1", TimeSpan.FromSeconds(30));
        await b.SetAsync(P + "axb", "2", TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { P + "a*b" }, (await b.GetByPrefixAsync(P + "a*")).Keys);
    }

    [RedisFact]
    public async Task Two_fleets_on_one_redis_see_each_other()
    {
        var cluster = "c" + Guid.NewGuid().ToString("N");
        using var a = new ServerNode(nodeId: "redis-a");
        using var b = new ServerNode(nodeId: "redis-b");
        var fa = new Fleet(Create(), a, new FleetOptions { Cluster = cluster, InternalAddress = "10.0.0.1:8080" });
        var fb = new Fleet(Create(), b, new FleetOptions { Cluster = cluster });
        await fa.HeartbeatAsync();
        await fb.HeartbeatAsync();
        await fa.HeartbeatAsync();
        Assert.Equal(new[] { "redis-a", "redis-b" }, fa.Nodes.Select(n => n.NodeId));
        Assert.Equal("10.0.0.1:8080", fb.Find("redis-a")!.InternalAddress);
        fa.Claim("player", "p1");
        await Task.Delay(100);
        Assert.Equal("redis-a", await fb.LocateAsync("player", "p1"));
        await fa.LeaveAsync();
        await fb.HeartbeatAsync();
        Assert.Null(fb.Find("redis-a"));
        Assert.Null(await fb.LocateAsync("player", "p1"));
    }

    public void Dispose()
    {
        if (_mux.IsValueCreated) _mux.Value.Dispose();
    }
}

public class FleetTests
{
    private readonly ManualUtc _clock = new();
    private readonly InMemoryFleetBackplane _backplane;

    public FleetTests() => _backplane = new InMemoryFleetBackplane(shared: true, () => _clock.Now);

    private (ServerNode Node, Fleet Fleet) Member(string id, double maxLoad = 0, ICapacityContributor? load = null, string region = "")
    {
        var node = new ServerNode(new ServerNodeOptions { MaxLoad = maxLoad }, load is null ? null : new[] { load }, nodeId: id);
        var fleet = new Fleet(_backplane, node, new FleetOptions
        {
            Cluster = "test",
            Region = region,
            InternalAddress = $"10.0.0.{id.Length}:8080",
            PublicAddress = "wss://play.example.com/n/{node-id}",
        }, utcNow: () => _clock.Now);
        return (node, fleet);
    }

    [Fact]
    public async Task Servers_see_each_other_through_their_heartbeats()
    {
        var (_, a) = Member("a", maxLoad: 10, load: new FakeContributor("rooms", 2, 4), region: "eu");
        var (_, b) = Member("b");
        Assert.False(a.IsMultiNode);
        Assert.Equal(new[] { "a" }, a.Nodes.Select(n => n.NodeId));

        a.SetStat("queue:duel", 3);
        await a.HeartbeatAsync();
        await b.HeartbeatAsync();
        Assert.True(b.IsMultiNode);
        var seen = b.Find("a")!;
        Assert.Equal("eu", seen.Region);
        Assert.Equal(4, seen.Load);
        Assert.Equal(10, seen.MaxLoad);
        Assert.Equal(3, seen.Stat("queue:duel"));
        Assert.Equal(0, seen.Stat("queue:other"));
        Assert.Equal("10.0.0.1:8080", seen.InternalAddress);
        Assert.Equal("wss://play.example.com/n/a", seen.PublicAddress);
        Assert.True(seen.Fits(6));
        Assert.False(seen.Fits(6.5));
        Assert.Equal(new[] { "a", "b" }, b.Nodes.Select(n => n.NodeId));

        // a only learns about b with its next heartbeat.
        Assert.False(a.IsMultiNode);
        await a.HeartbeatAsync();
        Assert.True(a.IsMultiNode);
    }

    [Fact]
    public async Task A_server_that_stops_beating_drops_out_and_one_that_leaves_is_gone_at_once()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        var (_, c) = Member("cc");
        await a.HeartbeatAsync();
        await c.HeartbeatAsync();
        await b.HeartbeatAsync();
        Assert.Equal(3, b.Nodes.Count);

        await c.LeaveAsync();
        await b.HeartbeatAsync();
        Assert.Equal(new[] { "a", "b" }, b.Nodes.Select(n => n.NodeId));

        // a crashes: after three missed heartbeats it is out of b's view, even before b's next beat.
        _clock.Advance(TimeSpan.FromSeconds(6.1));
        Assert.Null(b.Find("a"));
        Assert.False(b.IsMultiNode);
        await b.HeartbeatAsync();
        Assert.Equal(new[] { "b" }, b.Nodes.Select(n => n.NodeId));
    }

    [Fact]
    public async Task The_set_of_servers_changing_is_announced()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        var changes = 0;
        b.NodesChanged += () => changes++;
        await b.HeartbeatAsync();
        Assert.Equal(0, changes);
        await a.HeartbeatAsync();
        await b.HeartbeatAsync();
        await b.HeartbeatAsync();
        Assert.Equal(1, changes);
        await a.LeaveAsync();
        await b.HeartbeatAsync();
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Units_are_found_on_the_server_that_claimed_them_while_it_lives()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        await a.HeartbeatAsync();
        await b.HeartbeatAsync();

        a.Claim("player", "p1");
        Assert.Equal("a", await b.LocateAsync("player", "p1"));
        Assert.Equal("a", await a.LocateAsync("player", "p1"));
        Assert.Null(await b.LocateAsync("player", "p2"));
        Assert.Null(await b.LocateAsync("lobby", "p1")); // kinds are separate

        a.Release("player", "p1");
        Assert.Null(await b.LocateAsync("player", "p1"));

        // A claim of a crashed server points nowhere once the server is out of the view.
        a.Claim("player", "p3");
        _clock.Advance(TimeSpan.FromSeconds(7));
        await b.HeartbeatAsync();
        Assert.Null(await b.LocateAsync("player", "p3"));
    }

    [Fact]
    public async Task Claims_are_refreshed_by_the_heartbeat_and_removed_when_leaving()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        a.Claim("player", "p1");
        for (var i = 0; i < 80; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await a.HeartbeatAsync();
        }
        await b.HeartbeatAsync();
        Assert.Equal("a", await b.LocateAsync("player", "p1")); // 160 s > the 90 s claim life
        await a.LeaveAsync();
        Assert.Null(await _backplane.GetAsync("fleet:test:unit:player:p1"));
    }

    [Fact]
    public async Task Unique_claims_go_to_one_server_and_a_dead_owner_can_be_replaced()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        await a.HeartbeatAsync();
        await b.HeartbeatAsync();
        Assert.True(await a.TryClaimAsync("lobby", "ABCDE"));
        Assert.True(await a.TryClaimAsync("lobby", "ABCDE")); // already ours
        Assert.False(await b.TryClaimAsync("lobby", "ABCDE"));

        _clock.Advance(TimeSpan.FromSeconds(7)); // a is gone, its claim still stored
        await b.HeartbeatAsync();
        Assert.True(await b.TryClaimAsync("lobby", "ABCDE"));
        Assert.Equal("b", await b.LocateAsync("lobby", "ABCDE"));
    }

    [Fact]
    public async Task Work_handed_over_is_taken_once()
    {
        var (_, a) = Member("a");
        var (_, b) = Member("b");
        await a.PutHandoffAsync("b", "p1", new FleetHandoff("queue", new() { ["playlist"] = "duel", ["waited"] = "4.5" }, "a"));
        Assert.Null(await a.TakeHandoffAsync("p1")); // meant for b: a cannot take it
        var h = await b.TakeHandoffAsync("p1");
        Assert.NotNull(h);
        Assert.Equal("queue", h!.Kind);
        Assert.Equal("4.5", h.Get("waited"));
        Assert.Null(h.Get("nothing"));
        Assert.Equal("a", h.FromNode);
        Assert.Null(await b.TakeHandoffAsync("p1"));
        Assert.Null(await a.TakeHandoffAsync("p2"));
    }

    [Fact]
    public async Task Handoffs_expire()
    {
        var (_, a) = Member("a");
        await a.PutHandoffAsync("a", "p1", new FleetHandoff("queue", new(), "a"));
        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await a.TakeHandoffAsync("p1"));
    }

    [Fact]
    public async Task Fleets_of_different_clusters_do_not_mix()
    {
        var (nodeA, _) = Member("a");
        var a = new Fleet(_backplane, nodeA, new FleetOptions { Cluster = "one" }, utcNow: () => _clock.Now);
        var (nodeB, _) = Member("b");
        var b = new Fleet(_backplane, nodeB, new FleetOptions { Cluster = "two" }, utcNow: () => _clock.Now);
        await a.HeartbeatAsync();
        await b.HeartbeatAsync();
        Assert.False(b.IsMultiNode);
        a.Claim("player", "p");
        Assert.Null(await b.LocateAsync("player", "p"));
    }

    [Fact]
    public async Task A_broken_backplane_leaves_the_server_serving_alone()
    {
        var (_, a) = Member("a");
        var (nodeB, _) = Member("b");
        await a.HeartbeatAsync();
        var broken = new ThrowingBackplane(_backplane);
        var b = new Fleet(broken, nodeB, new FleetOptions { Cluster = "test" }, utcNow: () => _clock.Now);
        await b.HeartbeatAsync();
        Assert.True(b.IsMultiNode);
        broken.Down = true;
        await b.HeartbeatAsync(); // does not throw
        _clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False(b.IsMultiNode);
        Assert.Equal(new[] { "b" }, b.Nodes.Select(n => n.NodeId));
        b.Claim("player", "x"); // a failed background write is only logged
        await b.LeaveAsync();   // does not throw
    }

    [Fact]
    public void Self_follows_the_server_node()
    {
        var load = new FakeContributor("rooms", 1, 1);
        var (node, a) = Member("a", maxLoad: 2, load: load);
        Assert.True(a.Self.Accepting);
        load.Load = 2;
        Assert.Equal(ServerNodeState.Full, a.Self.State);
        Assert.False(a.Self.Fits(0));
        node.Register(new FakeParticipant()); // still running: the drain waits for it
        _ = node.DrainAsync();
        Assert.Equal(ServerNodeState.Draining, a.Self.State);
    }

    [Theory]
    [InlineData("localhost", "8080", "127.0.0.1:8080")]
    [InlineData("127.0.0.1", "9000", "127.0.0.1:9000")]
    [InlineData("game.internal", "8080", "game.internal:8080")]
    [InlineData("10.1.2.3", "8080", "10.1.2.3:8080")]
    [InlineData("localhost", "", null)]
    public void The_internal_address_follows_the_http_binding(string host, string port, string? expected) =>
        Assert.Equal(expected, Fleet.DetectInternalAddress(host, port));

    [Fact]
    public void A_wildcard_binding_uses_this_machines_address()
    {
        var detected = Fleet.DetectInternalAddress("0.0.0.0", "8080")!;
        Assert.EndsWith(":8080", detected);
        Assert.Equal((Fleet.MachineAddress()?.ToString() ?? "127.0.0.1") + ":8080", detected);
    }

    [Fact]
    public void The_DI_constructor_reads_its_config_and_expands_the_ip_placeholder()
    {
        using var node = new ServerNode(nodeId: "n1");
        var fleet = new Fleet(new InMemoryFleetBackplane(), node, cluster: " game ", region: "eu", heartbeatSeconds: 5,
            internalAddress: "{ip}:8000", publicAddress: "", nodeParam: "srv", httpHost: "127.0.0.1", httpPort: "8080");
        Assert.Equal("game", fleet.Cluster);
        Assert.Equal("eu", fleet.Region);
        Assert.Equal("srv", fleet.NodeParam);
        Assert.Equal(TimeSpan.FromSeconds(5), fleet.Options.Heartbeat);
        Assert.Equal((Fleet.MachineAddress()?.ToString() ?? "127.0.0.1") + ":8000", fleet.Options.InternalAddress);
        Assert.Null(fleet.Options.PublicAddress);
        Assert.False(fleet.Shared);

        var defaults = new Fleet(new InMemoryFleetBackplane(), node, null, null, 0, null, null, null, "localhost", "8080");
        Assert.Equal("default", defaults.Cluster);
        Assert.Equal("node", defaults.NodeParam);
        Assert.Equal(TimeSpan.FromSeconds(2), defaults.Options.Heartbeat);
        Assert.Equal("127.0.0.1:8080", defaults.Options.InternalAddress);
    }

    [Fact]
    public async Task The_heartbeat_service_beats_until_stopped_and_then_leaves()
    {
        using var node = new ServerNode(nodeId: "svc");
        var backplane = new InMemoryFleetBackplane(shared: true);
        var fleet = new Fleet(backplane, node, new FleetOptions { Cluster = "svc", Heartbeat = TimeSpan.FromMilliseconds(20) });
        var service = new FleetHeartbeatService(fleet);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.NotNull(await backplane.GetAsync("fleet:svc:node:svc"));
        await service.StopAsync(CancellationToken.None);
        Assert.Null(await backplane.GetAsync("fleet:svc:node:svc"));
    }

    private sealed class ThrowingBackplane(IFleetBackplane inner) : IFleetBackplane
    {
        public volatile bool Down;
        public string Kind => "flaky";
        public bool Shared => true;
        private Task Check() => Down ? Task.FromException(new InvalidOperationException("backplane down")) : Task.CompletedTask;
        public async Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Check(); await inner.SetAsync(key, value, ttl, ct); }
        public async Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken ct = default) { await Check(); await inner.SetManyAsync(values, ttl, ct); }
        public async Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) { await Check(); return await inner.SetIfAbsentAsync(key, value, ttl, ct); }
        public async Task<string?> GetAsync(string key, CancellationToken ct = default) { await Check(); return await inner.GetAsync(key, ct); }
        public async Task<string?> TakeAsync(string key, CancellationToken ct = default) { await Check(); return await inner.TakeAsync(key, ct); }
        public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken ct = default) { await Check(); return await inner.GetByPrefixAsync(prefix, ct); }
        public async Task DeleteAsync(string key, CancellationToken ct = default) { await Check(); await inner.DeleteAsync(key, ct); }
        public async Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken ct = default) { await Check(); return await inner.DeleteIfValueAsync(key, value, ct); }
    }
}
