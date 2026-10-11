/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Framework.Scaling;

/// <summary>The fleet services as Altruist's DI builds them: constructor selection and config values.</summary>
public class ServerNodeDiTests
{
    private static ServiceProvider Build(Dictionary<string, string?> config, Action<IServiceCollection>? extra = null, params (Type Service, Type Impl)[] types)
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var log = NullLogger.Instance;
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IConfiguration>(cfg);
        DependencyResolver.EnsureConverters(services, cfg, log);
        extra?.Invoke(services);
        foreach (var (service, impl) in types)
        {
            DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, impl);
            services.AddSingleton(service, sp => DependencyResolver.CreateWithConfiguration(sp, cfg, impl, log));
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_node_takes_its_budget_and_drain_settings_from_config()
    {
        using var sp = Build(new()
        {
            ["altruist:server:capacity:max-load"] = "24",
            ["altruist:server:drain:timeout"] = "330",
            ["altruist:server:drain:force-grace"] = "7.5",
        }, s => s.AddSingleton<ICapacityContributor>(new FakeContributor("rooms", 2, 6)),
            (typeof(IServerNode), typeof(ServerNode)));
        var node = (ServerNode)sp.GetRequiredService<IServerNode>();
        Assert.Equal(24, node.MaxLoad);
        Assert.Equal(TimeSpan.FromSeconds(330), node.DrainTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7.5), node.Options.ForceStopGrace);
        // Contributors registered as services are found without registering them by hand.
        Assert.Equal(6, node.Capacity().Load);
        Assert.Equal(ServerNodeState.Ready, node.State);
    }

    [Fact]
    public void Without_config_the_node_is_unlimited_with_a_30_second_drain()
    {
        using var sp = Build(new(), null, (typeof(IServerNode), typeof(ServerNode)));
        var node = sp.GetRequiredService<IServerNode>();
        Assert.Equal(0, node.MaxLoad);
        Assert.Equal(TimeSpan.FromSeconds(30), node.DrainTimeout);
        Assert.True(node.CanAccept(1000));
    }

    [Fact]
    public async Task The_shutdown_drain_can_be_switched_off_in_config()
    {
        var p = new FakeParticipant { Drained = true };
        using var sp = Build(new() { ["altruist:server:drain:on-shutdown"] = "false" },
            s => s.AddSingleton<IDrainParticipant>(p),
            (typeof(IServerNode), typeof(ServerNode)), (typeof(IHostedService), typeof(ServerDrainOnShutdown)));
        var hosted = (IHostedLifecycleService)sp.GetRequiredService<IHostedService>();
        await hosted.StoppingAsync(CancellationToken.None);
        Assert.Equal(0, p.Begins);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("auto", -1)]
    public void Step_workers_come_from_config(string value, int expected)
    {
        using var sp = Build(new() { ["altruist:game:engine:step-workers"] = value }, null, (typeof(IStepScheduler), typeof(StepScheduler)));
        var workers = sp.GetRequiredService<IStepScheduler>().Workers;
        Assert.Equal(expected == -1 ? Math.Max(1, Environment.ProcessorCount) : expected, workers);
    }

    [Fact]
    public void Step_workers_default_to_one()
    {
        using var sp = Build(new(), null, (typeof(IStepScheduler), typeof(StepScheduler)));
        Assert.Equal(1, sp.GetRequiredService<IStepScheduler>().Workers);
    }

    [Fact]
    public void The_fleet_is_built_from_config_on_the_default_backplane()
    {
        using var sp = Build(new()
        {
            ["altruist:server:fleet:cluster"] = "driftlink",
            ["altruist:server:fleet:region"] = "eu-central",
            ["altruist:server:fleet:internal-address"] = "{ip}:8000",
            ["altruist:server:fleet:public-address"] = "wss://play.example.com/n/{node-id}",
            ["altruist:server:http:host"] = "127.0.0.1",
            ["altruist:server:http:port"] = "8080",
        }, null,
            (typeof(IServerNode), typeof(ServerNode)), (typeof(IFleetBackplane), typeof(InMemoryFleetBackplane)), (typeof(IFleet), typeof(Fleet)));
        var fleet = (Fleet)sp.GetRequiredService<IFleet>();
        Assert.Equal("driftlink", fleet.Cluster);
        Assert.Equal("eu-central", fleet.Region);
        Assert.EndsWith(":8000", fleet.Options.InternalAddress);
        Assert.Equal($"wss://play.example.com/n/{fleet.NodeId}", fleet.Self.PublicAddress);
        Assert.False(fleet.Shared); // in-memory: a fleet of one
        Assert.Same(sp.GetRequiredService<IServerNode>().NodeId, fleet.NodeId);
    }

    [RedisFact]
    public async Task The_redis_backplane_is_built_from_its_connection_string()
    {
        using var sp = Build(new() { ["altruist:server:fleet:redis"] = RedisFactAttribute.Address }, null,
            (typeof(IFleetBackplane), typeof(global::Altruist.Redis.RedisFleetBackplane)));
        var backplane = sp.GetRequiredService<IFleetBackplane>();
        Assert.Equal("redis", backplane.Kind);
        Assert.True(backplane.Shared);
        var key = "di-test:" + Guid.NewGuid().ToString("N");
        await backplane.SetAsync(key, "ok", TimeSpan.FromSeconds(10));
        Assert.Equal("ok", await backplane.TakeAsync(key));
    }

    [Fact]
    public void An_unreachable_redis_does_not_stop_the_server_from_starting()
    {
        using var sp = Build(new() { ["altruist:server:fleet:redis"] = "127.0.0.1:1,connectTimeout=200" }, null,
            (typeof(IFleetBackplane), typeof(global::Altruist.Redis.RedisFleetBackplane)));
        Assert.Equal("redis", sp.GetRequiredService<IFleetBackplane>().Kind);
    }

    [Fact]
    public void Worlds_count_as_units_without_using_up_the_budget_unless_configured()
    {
        using var none = Build(new(), null, (typeof(ICapacityContributor), typeof(WorldCapacityContributor)));
        var contributor = none.GetRequiredService<ICapacityContributor>();
        Assert.Equal("worlds", contributor.Kind);
        Assert.Equal(CapacitySample.Empty, contributor.Sample()); // no organizer in this mode

        var organizer = new Moq.Mock<global::Altruist.Gaming.TwoD.IGameWorldOrganizer2D>();
        organizer.Setup(o => o.GetAllWorlds()).Returns(new List<global::Altruist.Gaming.TwoD.IGameWorldManager2D>
        {
            new Moq.Mock<global::Altruist.Gaming.TwoD.IGameWorldManager2D>().Object,
            new Moq.Mock<global::Altruist.Gaming.TwoD.IGameWorldManager2D>().Object,
        });
        using var two = Build(new() { ["altruist:server:capacity:world-load"] = "1.5" },
            s => s.AddSingleton(organizer.Object), (typeof(ICapacityContributor), typeof(WorldCapacityContributor)));
        Assert.Equal(new CapacitySample(2, 3), two.GetRequiredService<ICapacityContributor>().Sample());
    }
}
