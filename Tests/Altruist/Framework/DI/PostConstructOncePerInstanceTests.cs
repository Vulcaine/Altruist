/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;

using Microsoft.Extensions.DependencyInjection;

namespace Tests.Framework.DI;

public interface ICountedHookService
{
    int HookRuns { get; }
}

public sealed class CountedHookService : ICountedHookService
{
    public int HookRuns { get; private set; }

    [PostConstruct]
    public void Hook() => HookRuns++;
}

/// <summary>
/// [PostConstruct] ran once per implementation type per process, so a second container built in the same process
/// (an [AltruistTest] class container and then the live server of an [AltruistIntegrationTest] class) got fresh
/// singletons whose hooks never ran: ServerStatus.Configure never connected the database and every request answered
/// 503 "Service is not ready." forever.
/// </summary>
public sealed class PostConstructOncePerInstanceTests
{
    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<CountedHookService>();
        services.AddSingleton<ICountedHookService>(sp => sp.GetRequiredService<CountedHookService>());
        return services;
    }

    private static CountedHookService RunHooksInNewProvider(IServiceCollection services, out ServiceProvider provider)
    {
        provider = services.BuildServiceProvider();
        AltruistDI.RunPostConstructsAsync(provider, services).GetAwaiter().GetResult();
        return provider.GetRequiredService<CountedHookService>();
    }

    [Fact]
    public void Hook_runs_on_the_singletons_of_a_second_provider()
    {
        var services = CreateServices();
        var first = RunHooksInNewProvider(services, out var firstProvider);
        var second = RunHooksInNewProvider(services, out var secondProvider);
        using (firstProvider)
        using (secondProvider)
        {
            Assert.Equal(1, first.HookRuns);
            Assert.Equal(1, second.HookRuns);
        }
    }

    [Fact]
    public void Hook_runs_once_for_an_instance_registered_under_two_service_types()
    {
        var services = CreateServices();
        var instance = RunHooksInNewProvider(services, out var provider);
        using (provider)
        {
            Assert.Equal(1, instance.HookRuns);
        }
    }

    [Fact]
    public void Hook_does_not_run_again_when_the_same_provider_is_processed_twice()
    {
        var services = CreateServices();
        var instance = RunHooksInNewProvider(services, out var provider);
        using (provider)
        {
            AltruistDI.RunPostConstructsAsync(provider, services).GetAwaiter().GetResult();
            Assert.Equal(1, instance.HookRuns);
        }
    }
}
