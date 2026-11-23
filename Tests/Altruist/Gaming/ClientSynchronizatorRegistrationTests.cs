using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming;

/// <summary>
/// With the gaming module on, <see cref="IAltruistRouter.Synchronize"/> must be the working
/// <see cref="GameClientSynchronizator"/> whichever assembly the scan reaches first — the core
/// placeholder throws <see cref="NotImplementedException"/> on every send.
/// </summary>
public class ClientSynchronizatorRegistrationTests
{
    private static IServiceCollection Register(params Type[] order)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:server:transport:mode"] = "tcp",
            ["altruist:game:engine:framerateHz"] = "30",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton(Mock.Of<ICodec>());
        services.AddSingleton(Mock.Of<IConnectionStore>());
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in order)
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        return services;
    }

    private static IClientSynchronizator Resolve(IServiceCollection services)
        => services.BuildServiceProvider().GetRequiredService<IClientSynchronizator>();

    [Fact]
    public void gaming_synchronizator_wins_when_core_is_scanned_first()
    {
        var services = Register(typeof(ClientSynchronizator), typeof(GameClientSynchronizator));
        Assert.IsType<GameClientSynchronizator>(Resolve(services));
    }

    [Fact]
    public void gaming_synchronizator_wins_when_gaming_is_scanned_first()
    {
        var services = Register(typeof(GameClientSynchronizator), typeof(ClientSynchronizator));
        Assert.IsType<GameClientSynchronizator>(Resolve(services));
    }
}
