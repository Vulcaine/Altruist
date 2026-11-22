using Altruist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DI.Tests;

// A consumer processed before its dependency: the planner registers the dependency first.
[Service] public class PlannedFirstConsumer
{
    public PlannedFirstDependency Dependency { get; }
    public PlannedFirstConsumer(PlannedFirstDependency dependency) => Dependency = dependency;
}

[Service] public class PlannedFirstDependency
{
    public static int Constructed;
    public PlannedFirstDependency() => Interlocked.Increment(ref Constructed);
}

public interface IPlannedThing { }

[Service(typeof(IPlannedThing))] public class PlannedThing : IPlannedThing
{
    public static int Constructed;
    public PlannedThing() => Interlocked.Increment(ref Constructed);
}

[Service] public class PlannedThingConsumer
{
    public IPlannedThing Thing { get; }
    public PlannedThingConsumer(IPlannedThing thing) => Thing = thing;
}

/// <summary>
/// A [Service] that the dependency planner had already registered (as the dependency of a service
/// scanned earlier) got a second descriptor from its own attribute: two singletons per boot, and
/// an IEnumerable&lt;T&gt; listing the service twice.
/// </summary>
public class PlannedRegistrationDedupTests
{
    private static ServiceCollection Register(params Type[] types)
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().Build();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IConfiguration>(cfg);
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in types)
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        return services;
    }

    [Fact]
    public void A_dependency_registered_by_the_planner_first_gets_one_descriptor_and_one_instance()
    {
        var services = Register(typeof(PlannedFirstConsumer), typeof(PlannedFirstDependency));
        Assert.Single(services, d => d.ServiceType == typeof(PlannedFirstDependency));

        var before = PlannedFirstDependency.Constructed;
        using var sp = services.BuildServiceProvider();
        var consumer = sp.GetRequiredService<PlannedFirstConsumer>();
        Assert.Same(consumer.Dependency, sp.GetRequiredService<PlannedFirstDependency>());
        Assert.Single(sp.GetServices<PlannedFirstDependency>());
        Assert.Equal(1, PlannedFirstDependency.Constructed - before);
    }

    [Fact]
    public void An_interface_service_registered_by_the_planner_first_resolves_to_one_instance()
    {
        var services = Register(typeof(PlannedThingConsumer), typeof(PlannedThing));
        Assert.Single(services, d => d.ServiceType == typeof(IPlannedThing));

        var before = PlannedThing.Constructed;
        using var sp = services.BuildServiceProvider();
        var thing = sp.GetRequiredService<IPlannedThing>();
        Assert.Same(thing, sp.GetRequiredService<PlannedThing>());
        Assert.Same(thing, sp.GetRequiredService<PlannedThingConsumer>().Thing);
        Assert.Single(sp.GetServices<IPlannedThing>());
        Assert.Equal(1, PlannedThing.Constructed - before);
    }

    [Fact]
    public void Registration_order_dependency_first_is_unchanged()
    {
        var services = Register(typeof(PlannedFirstDependency), typeof(PlannedFirstConsumer));
        Assert.Single(services, d => d.ServiceType == typeof(PlannedFirstDependency));
        using var sp = services.BuildServiceProvider();
        Assert.Same(sp.GetRequiredService<PlannedFirstConsumer>().Dependency, sp.GetRequiredService<PlannedFirstDependency>());
    }
}
