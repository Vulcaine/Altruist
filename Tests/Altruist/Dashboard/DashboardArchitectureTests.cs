using Altruist;
using Altruist.Dashboard;
using Altruist.Transport;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Dashboard;

public sealed class ArchitectureStoresTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IReadOnlyList<ArchitectureStoreDto> Describe(Dictionary<string, string?> values, string machine = "m1", string? backplane = null,
        params ArchitectureVaultBinding[] vaults) =>
        ArchitectureStores.Describe(Config(values), new ArchitectureServerScope(machine, "node-1", "game", backplane), vaults);

    private static Dictionary<string, string?> Postgres(string host = "db.internal", string port = "5432") => new()
    {
        ["altruist:persistence:database:provider"] = "postgres",
        ["altruist:persistence:database:host"] = host,
        ["altruist:persistence:database:port"] = port,
        ["altruist:persistence:database:database"] = "game",
        ["altruist:persistence:database:password"] = "secret-db",
    };

    [Fact]
    public void Redis_endpoints_leave_out_credentials_and_options()
    {
        var endpoints = ArchitectureStores.Endpoints("redis-a:6380, redis-b ,password=hunter2,ssl=true", 6379);

        Assert.Equal(new[] { "redis-a:6380", "redis-b:6379" }, endpoints);
    }

    [Fact]
    public void A_redis_that_is_cache_and_backplane_is_one_physical_store()
    {
        var stores = Describe(new()
        {
            ["altruist:persistence:cache:provider"] = "redis",
            ["altruist:persistence:redis:connection-string"] = "redis.internal:6379,password=p",
            ["altruist:server:fleet:redis"] = "redis.internal:6379",
        }, backplane: "redis");

        var cache = Assert.Single(stores, s => s.Use == "cache");
        var backplane = Assert.Single(stores, s => s.Use == "backplane");
        Assert.Equal(cache.Target, backplane.Target);
        Assert.NotEqual(cache.Id, backplane.Id);
        Assert.DoesNotContain(stores, s => s.Id.Contains("password") || s.Endpoints.Any(e => e.Contains('=')));
    }

    [Fact]
    public void Fleets_of_two_clusters_on_one_redis_are_two_backplane_uses_of_one_store()
    {
        var values = new Dictionary<string, string?> { ["altruist:server:fleet:redis"] = "redis.internal:6379" };
        var game = Describe(values, backplane: "redis").Single();
        var channel = ArchitectureStores.Describe(Config(values), new ArchitectureServerScope("m1", "node-2", "game-ch2", "redis"), Array.Empty<ArchitectureVaultBinding>()).Single();

        Assert.Equal(game.Target, channel.Target);
        Assert.NotEqual(game.Id, channel.Id);
        Assert.Equal(("game", "game-ch2"), (game.Name, channel.Name));
    }

    [Fact]
    public void Database_instances_are_listed_with_their_roles_and_vaults()
    {
        var values = Postgres();
        values["altruist:persistence:database:instances:0:name"] = "replica";
        values["altruist:persistence:database:instances:0:host"] = "db-ro.internal";
        values["altruist:persistence:database:instances:0:database"] = "game";
        values["altruist:persistence:database:instances:0:role"] = "readonly";

        var stores = Describe(values, vaults: new[]
        {
            new ArchitectureVaultBinding("game.players", ""),
            new ArchitectureVaultBinding("game.leaderboard", "replica"),
            new ArchitectureVaultBinding("game.orphan", "no-such-instance"),
        });

        var main = Assert.Single(stores, s => s.Name == "default");
        var replica = Assert.Single(stores, s => s.Name == "replica");
        Assert.Equal("readwrite", main.Role);
        Assert.Equal("readonly", replica.Role);
        Assert.Equal(new[] { "game.orphan", "game.players" }, main.Vaults);
        Assert.Equal(new[] { "game.leaderboard" }, replica.Vaults);
        Assert.Equal(new[] { "db-ro.internal:5432" }, replica.Endpoints);
    }

    [Fact]
    public void An_instance_on_the_default_database_shares_its_target()
    {
        var values = Postgres();
        values["altruist:persistence:database:instances:0:name"] = "primary";
        values["altruist:persistence:database:instances:0:host"] = "db.internal";
        values["altruist:persistence:database:instances:0:database"] = "game";

        var stores = Describe(values);

        Assert.Single(stores.Select(s => s.Target).Distinct());
        Assert.Equal(2, stores.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Loopback_endpoints_are_the_same_store_on_one_machine_only()
    {
        var viaName = Describe(Postgres("localhost", "5880"), machine: "box-a").Single();
        var viaIp = Describe(Postgres("127.0.0.1", "5880"), machine: "box-a").Single();
        var elsewhere = Describe(Postgres("127.0.0.1", "5880"), machine: "box-b").Single();

        Assert.Equal(viaName.Target, viaIp.Target);
        Assert.NotEqual(viaIp.Target, elsewhere.Target);
    }

    [Fact]
    public void In_memory_stores_belong_to_their_server()
    {
        var stores = Describe(new() { ["altruist:persistence:cache:provider"] = "inmemory" }, backplane: "memory");

        Assert.All(stores, s => Assert.True(s.Local));
        Assert.All(stores, s => Assert.EndsWith("@node-1", s.Target));
    }

    [Fact]
    public void A_server_without_persistence_uses_no_store()
    {
        Assert.Empty(Describe(new()));
    }
}

public sealed class ArchitectureCardTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["altruist:persistence:cache:provider"] = "inmemory" })
        .Build();

    private static (Fleet Fleet, ArchitectureCardBuilder Builder) Member(InMemoryFleetBackplane backplane, string id, string cluster = "arch")
    {
        var node = new ServerNode(nodeId: id);
        var fleet = new Fleet(backplane, node, new FleetOptions { Cluster = cluster, InternalAddress = $"10.0.0.1:{id.Length}" });
        return (fleet, new ArchitectureCardBuilder(Config, node, Array.Empty<ITransport>(), fleet));
    }

    [Fact]
    public async Task The_dashboard_of_one_server_shows_the_cards_of_the_others()
    {
        var backplane = new InMemoryFleetBackplane(shared: true);
        var a = Member(backplane, "server-a");
        var b = Member(backplane, "server-b");
        var publisher = new ArchitectureCardPublisher(b.Builder, b.Fleet, backplane, NullLoggerFactory.Instance);
        var controller = new ArchitectureDashboardController(a.Builder, a.Fleet, backplane, NullLoggerFactory.Instance);

        await publisher.StartAsync(CancellationToken.None);
        var key = ArchitectureCardPublisher.KeyPrefix + "arch:server-b";
        await WaitForAsync(backplane, key);

        var report = Assert.IsType<ArchitectureReportDto>(Assert.IsType<OkObjectResult>((await controller.Get(CancellationToken.None)).Result).Value);

        Assert.Equal("server-a", report.Self);
        Assert.Equal(new[] { "server-a", "server-b" }, report.Cards.Select(c => c.NodeId));
        Assert.Equal("10.0.0.1:8", report.Cards[1].InternalAddress);
        Assert.Contains(report.Cards[1].Stores, s => s.Use == "backplane" && s.Provider == "memory");

        await publisher.StopAsync(CancellationToken.None);
        Assert.Null(await backplane.GetAsync(key));
    }

    [Fact]
    public async Task Servers_of_another_cluster_on_the_backplane_are_shown_with_their_cluster()
    {
        var backplane = new InMemoryFleetBackplane(shared: true);
        var a = Member(backplane, "server-a");
        var channel = Member(backplane, "channel-2", cluster: "arch-ch2");
        var publisher = new ArchitectureCardPublisher(channel.Builder, channel.Fleet, backplane, NullLoggerFactory.Instance);
        var controller = new ArchitectureDashboardController(a.Builder, a.Fleet, backplane, NullLoggerFactory.Instance);

        await publisher.StartAsync(CancellationToken.None);
        await WaitForAsync(backplane, ArchitectureCardPublisher.KeyPrefix + "arch-ch2:channel-2");
        var report = Assert.IsType<ArchitectureReportDto>(Assert.IsType<OkObjectResult>((await controller.Get(CancellationToken.None)).Result).Value);
        await publisher.StopAsync(CancellationToken.None);

        var other = Assert.Single(report.Cards, c => c.NodeId == "channel-2");
        Assert.Equal("arch-ch2", other.Cluster);
        Assert.Equal("Ready", other.State);
        Assert.DoesNotContain(report.Nodes, n => n.NodeId == "channel-2");
    }

    private static async Task WaitForAsync(IFleetBackplane backplane, string key)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await backplane.GetAsync(key) is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task A_server_alone_shows_only_its_own_card()
    {
        var a = Member(new InMemoryFleetBackplane(), "solo");
        var controller = new ArchitectureDashboardController(a.Builder, a.Fleet, new InMemoryFleetBackplane(), NullLoggerFactory.Instance);

        var report = Assert.IsType<ArchitectureReportDto>(Assert.IsType<OkObjectResult>((await controller.Get(CancellationToken.None)).Result).Value);

        Assert.False(report.Shared);
        Assert.Equal("solo", Assert.Single(report.Cards).NodeId);
    }
}

public sealed class DashboardAppFilesTests
{
    [Fact]
    public void Every_asset_the_app_page_references_is_embedded()
    {
        var files = DashboardStartupFilter.AppFiles();
        var index = files.GetFileInfo("index.html");
        Assert.True(index.Exists);
        using var reader = new StreamReader(index.CreateReadStream());
        var assets = System.Text.RegularExpressions.Regex.Matches(reader.ReadToEnd(), @"(?:src|href)=""\./(assets/[^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(assets);
        Assert.All(assets, asset => Assert.True(files.GetFileInfo(asset).Exists, $"{asset} is not embedded"));
    }

    [Fact]
    public void The_lazily_loaded_world_viewer_is_embedded()
    {
        Assert.True(DashboardStartupFilter.AppFiles().GetFileInfo("assets/WorldsView.js").Exists);
    }
}
