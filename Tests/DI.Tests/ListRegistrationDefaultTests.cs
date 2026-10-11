using Altruist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DI.Tests;

public interface IListedThing
{
    string Name { get; }
}

[Service(typeof(IListedThing))]
public class DefaultListedThing : IListedThing
{
    public string Name => "default";
}

[Service(typeof(IListedThing))]
[ConditionalOnConfig("things", KeyField = "name")]
public class ConfiguredListedThing : IListedThing
{
    public ConfiguredListedThing([AppConfigValue("*:name")] string name) => Name = name;
    public string Name { get; }
}

/// <summary>
/// List-style registrations (one keyed service per config item) also register each item unkeyed so that
/// IEnumerable&lt;T&gt; lists them. Those unkeyed forwards used to be appended, so a plain GetService&lt;T&gt; returned
/// the last list item instead of the regular registration whenever the list was registered after it.
/// </summary>
public class ListRegistrationDefaultTests
{
    private static ServiceProvider Register(params Type[] types)
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["things:0:name"] = "a", ["things:1:name"] = "b" })
            .Build();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in types)
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_regular_registration_stays_the_unkeyed_service_whatever_the_order(bool listFirst)
    {
        using var sp = listFirst
            ? Register(typeof(ConfiguredListedThing), typeof(DefaultListedThing))
            : Register(typeof(DefaultListedThing), typeof(ConfiguredListedThing));

        Assert.Equal("default", sp.GetRequiredService<IListedThing>().Name);
        Assert.Equal("a", sp.GetRequiredKeyedService<IListedThing>("a").Name);
        Assert.Equal("b", sp.GetRequiredKeyedService<IListedThing>("b").Name);
        Assert.Equal(new[] { "a", "b", "default" }, sp.GetServices<IListedThing>().Select(t => t.Name).Order());
    }
}
