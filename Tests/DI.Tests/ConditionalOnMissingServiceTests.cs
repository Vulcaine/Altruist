using Altruist;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DI.Tests;

public interface IMissingOnlyDefault { }

[Service(typeof(IMissingOnlyDefault))]
[ConditionalOnMissingService(typeof(IMissingOnlyDefault))]
public class MissingOnlyDefault : IMissingOnlyDefault { }

public interface IMissingReplaceable { }

[Service(typeof(IMissingReplaceable))]
[ConditionalOnMissingService(typeof(IMissingReplaceable))]
public class MissingReplaceableDefault : IMissingReplaceable { }

[Service(typeof(IMissingReplaceable))]
public class MissingReplaceableApp : IMissingReplaceable { }

public interface IMissingGated { }

[Service(typeof(IMissingGated))]
[ConditionalOnMissingService(typeof(IMissingGated))]
public class MissingGatedDefault : IMissingGated { }

[Service(typeof(IMissingGated))]
[ConditionalOnConfig("test:missing:gated", havingValue: "on")]
public class MissingGatedApp : IMissingGated { }

public interface IMissingManual { }

[Service(typeof(IMissingManual))]
[ConditionalOnMissingService(typeof(IMissingManual))]
public class MissingManualDefault : IMissingManual { }

public class MissingManualApp : IMissingManual { }

/// <summary>
/// [ConditionalOnMissingService]: a framework default (e.g. the engine's WorldCoordinator) that an
/// application replaces by registering its own implementation of the service.
/// </summary>
public class ConditionalOnMissingServiceTests
{
    private static ServiceCollection Register(IConfiguration cfg, ServiceCollection? services = null, params Type[] types)
    {
        services ??= new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton(cfg);
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in types)
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        return services;
    }

    private static IConfiguration Cfg(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void The_default_is_registered_when_nothing_else_provides_the_service()
    {
        var services = Register(Cfg(), null, typeof(MissingOnlyDefault));
        using var sp = services.BuildServiceProvider();
        Assert.IsType<MissingOnlyDefault>(sp.GetRequiredService<IMissingOnlyDefault>());
    }

    [Fact]
    public void Another_service_class_replaces_the_default_whatever_the_scan_order()
    {
        foreach (var order in new[] { new[] { typeof(MissingReplaceableDefault), typeof(MissingReplaceableApp) },
                                      new[] { typeof(MissingReplaceableApp), typeof(MissingReplaceableDefault) } })
        {
            var services = Register(Cfg(), null, order);
            using var sp = services.BuildServiceProvider();
            Assert.IsType<MissingReplaceableApp>(Assert.Single(sp.GetServices<IMissingReplaceable>()));
        }
    }

    [Fact]
    public void A_replacement_switched_off_by_config_does_not_count()
    {
        var off = Register(Cfg(), null, typeof(MissingGatedDefault), typeof(MissingGatedApp));
        using (var sp = off.BuildServiceProvider())
            Assert.IsType<MissingGatedDefault>(Assert.Single(sp.GetServices<IMissingGated>()));

        var on = Register(Cfg(("test:missing:gated", "on")), null, typeof(MissingGatedDefault), typeof(MissingGatedApp));
        using (var sp = on.BuildServiceProvider())
            Assert.IsType<MissingGatedApp>(Assert.Single(sp.GetServices<IMissingGated>()));
    }

    [Fact]
    public void A_registration_made_by_hand_replaces_the_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMissingManual, MissingManualApp>();
        Register(Cfg(), services, typeof(MissingManualDefault));
        using var sp = services.BuildServiceProvider();
        Assert.IsType<MissingManualApp>(Assert.Single(sp.GetServices<IMissingManual>()));
    }
}
