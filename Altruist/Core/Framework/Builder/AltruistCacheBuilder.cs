using Altruist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

// Step 2: Choose Cache
public class AltruistCacheBuilder : IAfterConnectionBuilder
{
    protected readonly IServiceCollection Services;
    protected readonly IAltruistContext Settings;
    private string[] _args;

    internal AltruistCacheBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
    {
        Services = services;
        Settings = settings;
        _args = args;
    }

    public AltruistWebApplicationBuilder WebApp(Func<WebApplicationBuilder, WebApplicationBuilder>? setup = null)
    {
        var app = WebApiHelper.Create(_args, Services);
        if (setup != null)
        {
            return new AltruistWebApplicationBuilder(setup(app), Settings);
        }
        return new AltruistWebApplicationBuilder(app, Settings);
    }

    public AltruistDatabaseBuilder NoCache()
    {
        return new AltruistDatabaseBuilder(Services, Settings, _args);
    }

    public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
    {
        token.Configuration.Configure(Services);
        var setupInstance = Services.BuildServiceProvider()
            .GetRequiredService<TCacheConnectionSetup>();
        SetupCache(token, setupInstance);
        return new AltruistDatabaseBuilder(Services, Settings, _args);
    }

    public AltruistDatabaseBuilder SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, Func<TCacheConnectionSetup, TCacheConnectionSetup>? setup) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
    {
        var serviceCollection = Services.AddSingleton<TCacheConnectionSetup>();
        var setupInstance = serviceCollection.BuildServiceProvider().GetService<TCacheConnectionSetup>();

        if (setup != null)
        {
            setupInstance = setup(setupInstance!);
        }

        SetupCache(token, setupInstance!);
        return new AltruistDatabaseBuilder(Services, Settings, _args);
    }

    private void SetupCache<TCacheConnectionSetup>(ICacheServiceToken token, TCacheConnectionSetup instance) where TCacheConnectionSetup : class, ICacheConnectionSetup<TCacheConnectionSetup>
    {
        token.Configuration.Configure(Services);
        Services.AddSingleton<TCacheConnectionSetup>();
        instance.Build(Settings);
        // readding the built instance
        Services.AddSingleton(instance);
        Settings.CacheToken = token;
    }
}