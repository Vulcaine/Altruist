/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist;
using Altruist.Gaming;
using Altruist.Engine;
using Altruist.Gaming.TwoD;
using Altruist.Physx;
using Altruist.Physx.TwoD;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace Tests.Gaming.World.TwoD;

/// <summary>
/// GameWorldOrganizer2D and VisibilityTracker2D used to take each other as constructor
/// dependencies (a DI cycle: the 2D game could not be resolved at all). The organizer now gets the
/// tracker via SetVisibilityTracker, wired by the tracker's [PostConstruct] WireOrganizer or by
/// EngineStartupConfiguration, and the tracker tolerates running before it is wired.
/// </summary>
public sealed class VisibilityWiringRegressionTests
{
    private static GameWorldOrganizer2D NewOrganizer()
    {
        var partitioner = new Mock<IWorldPartitioner2D>();
        partitioner.Setup(p => p.CalculatePartitions(It.IsAny<IWorldIndex2D>())).Returns(new List<WorldPartition2D>());
        return new GameWorldOrganizer2D(partitioner.Object, new Mock<ICacheProvider>().Object,
            new Mock<IPhysxWorldEngineFactory2D>().Object, Array.Empty<IWorldIndex2D>());
    }

    [Fact]
    public void Neither_constructor_depends_on_the_other()
    {
        Assert.DoesNotContain(typeof(GameWorldOrganizer2D).GetConstructors().SelectMany(c => c.GetParameters()),
            p => typeof(IVisibilityTracker).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(typeof(VisibilityTracker2D).GetConstructors().SelectMany(c => c.GetParameters()),
            p => typeof(IGameWorldOrganizer2D).IsAssignableFrom(p.ParameterType));
    }

    [Fact]
    public void Both_resolve_from_one_container_without_a_cycle()
    {
        var services = new ServiceCollection();
        var partitioner = new Mock<IWorldPartitioner2D>();
        partitioner.Setup(p => p.CalculatePartitions(It.IsAny<IWorldIndex2D>())).Returns(new List<WorldPartition2D>());
        services.AddSingleton(partitioner.Object);
        services.AddSingleton(new Mock<ICacheProvider>().Object);
        services.AddSingleton(new Mock<IPhysxWorldEngineFactory2D>().Object);
        services.AddSingleton<IGameWorldOrganizer2D, GameWorldOrganizer2D>();
        services.AddSingleton<IVisibilityTracker>(_ => new VisibilityTracker2D());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var organizer = provider.GetRequiredService<IGameWorldOrganizer2D>();
        var tracker = (VisibilityTracker2D)provider.GetRequiredService<IVisibilityTracker>();

        tracker.WireOrganizer(organizer); // what the framework's [PostConstruct] does
        Assert.Same(tracker, GetTracker(organizer));
    }

    [Fact]
    public void WireOrganizer_is_a_PostConstruct_hook_and_wires_both_directions_once()
    {
        var method = typeof(VisibilityTracker2D).GetMethod(nameof(VisibilityTracker2D.WireOrganizer))!;
        Assert.NotNull(method.GetCustomAttribute<PostConstructAttribute>());

        var organizer = new Mock<IGameWorldOrganizer2D>();
        organizer.Setup(o => o.GetAllWorlds()).Returns(Array.Empty<IGameWorldManager2D>());
        var tracker = new VisibilityTracker2D();

        tracker.WireOrganizer(organizer.Object);
        tracker.WireOrganizer(organizer.Object);

        organizer.Verify(o => o.SetVisibilityTracker(tracker), Times.Once);
        tracker.Tick();
        organizer.Verify(o => o.GetAllWorlds(), Times.Once);
    }

    [Fact]
    public void An_unwired_tracker_ticks_and_removes_observers_without_throwing()
    {
        var tracker = new VisibilityTracker2D(1000f);
        tracker.Tick();
        tracker.RemoveObserver("nobody");
        Assert.Equal(1000f, tracker.ViewRange);
    }

    [Fact]
    public void Organizer_ticks_the_tracker_it_was_given()
    {
        var organizer = NewOrganizer();
        var tracker = new VisibilityTracker2D();
        organizer.SetVisibilityTracker(tracker);
        tracker.SetOrganizer(organizer);

        Assert.Same(tracker, GetTracker(organizer));
        organizer.SetVisibilityTracker(null);
        Assert.Null(GetTracker(organizer));
    }

    private static object? GetTracker(object organizer) =>
        typeof(GameWorldOrganizer2D).GetField("_visibilityTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(organizer);
}

/// <summary>
/// The engine's tick rate key: docs and examples use <c>altruist:game:engine:frequency</c>, the
/// engine only read <c>framerateHz</c> (with a non-null default that made an alias impossible).
/// </summary>
public sealed class EngineFrequencyRegressionTests
{
    private static AltruistEngine NewEngine(int? framerateHz, int? frequency) =>
        new(new Mock<IServerStatus>().Object, new ServiceCollection().BuildServiceProvider(),
            new Mock<IGameWorldOrganizer>().Object, framerateHz: framerateHz, frequency: frequency);

    private static long RateFor(int hz) => new CycleRate(hz, CycleUnit.Ticks).Value;

    [Fact]
    public void Frequency_key_is_accepted_as_an_alias()
    {
        Assert.Equal(RateFor(60), NewEngine(null, 60).Rate.Value);
    }

    [Fact]
    public void FramerateHz_wins_over_the_alias_and_30Hz_is_the_default()
    {
        Assert.Equal(RateFor(20), NewEngine(20, 60).Rate.Value);
        Assert.Equal(RateFor(30), NewEngine(null, null).Rate.Value);
    }

    [Fact]
    public void Both_config_keys_are_bound_on_the_constructor()
    {
        var keys = typeof(AltruistEngine).GetConstructors().Single().GetParameters()
            .Select(p => p.GetCustomAttribute<AppConfigValueAttribute>())
            .Where(a => a is not null)
            .ToDictionary(a => a!.Path, a => a!.Default);
        Assert.True(keys.ContainsKey("altruist:game:engine:frequency"));
        Assert.True(keys.ContainsKey("altruist:game:engine:framerateHz"));
        // No default on framerateHz, otherwise the alias could never take effect.
        Assert.Null(keys["altruist:game:engine:framerateHz"]);
    }
}
