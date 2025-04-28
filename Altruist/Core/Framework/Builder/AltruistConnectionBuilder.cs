using Altruist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

// Step 1: Choose Transport
public class AltruistConnectionBuilder
{
    public readonly IServiceCollection Services;
    protected readonly IAltruistContext Settings;

    private string[] _args;

    public AltruistConnectionBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
    {
        Services = services;
        Settings = settings;
        _args = args;
    }

    public AltruistCacheBuilder SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, Func<TTransportConnectionSetup, TTransportConnectionSetup> setup) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
    {
        Settings.TransportToken = token;
        var serviceCollection = Services.AddSingleton<TTransportConnectionSetup>();
        var setupInstance = serviceCollection.BuildServiceProvider().GetService<TTransportConnectionSetup>();

        if (setup != null)
        {
            setupInstance = setup(setupInstance!);
        }

        SetupTransport(token, setupInstance!);
        return new AltruistCacheBuilder(Services, Settings, _args);
    }

    private void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token, TTransportConnectionSetup instance) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
    {
        Settings.TransportToken = token;
        token.Configuration.Configure(Services);
        instance.Build(Settings);
        // readding the built instance
        Services.AddSingleton(instance);
    }

    public void SetupTransport<TTransportConnectionSetup>(ITransportServiceToken token) where TTransportConnectionSetup : class, ITransportConnectionSetup<TTransportConnectionSetup>
    {
        token.Configuration.Configure(Services);
        var setupInstance = Services.BuildServiceProvider()
            .GetRequiredService<TTransportConnectionSetup>();
        SetupTransport(token, setupInstance);
    }
}

public interface IAfterConnectionBuilder
{
    AltruistWebApplicationBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null);
    AltruistDatabaseBuilder NoCache();
    AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>;
    AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, Func<TCacheConnectionSetup, TCacheConnectionSetup>? setup) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>;
}
