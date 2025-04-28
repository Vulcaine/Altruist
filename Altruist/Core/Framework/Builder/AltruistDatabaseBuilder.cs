using Altruist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

// Step 3: Choose Database
public class AltruistDatabaseBuilder
{
    protected readonly IServiceCollection Services;
    protected readonly IAltruistContext Settings;
    private string[] _args;

    internal AltruistDatabaseBuilder(IServiceCollection services, IAltruistContext settings, string[] args)
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


    public AltruistApplicationBuilder NoDatabase()
    {
        return new AltruistApplicationBuilder(Services, Settings, _args);
    }

    public AltruistApplicationBuilder SetupDatabase<TDatabaseConnectionSetup>(IDatabaseServiceToken token, Func<TDatabaseConnectionSetup, TDatabaseConnectionSetup>? setup = null) where TDatabaseConnectionSetup : class, IDatabaseConnectionSetup<TDatabaseConnectionSetup>
    {
        var serviceCollection = Services.AddSingleton<TDatabaseConnectionSetup>();
        var setupInstance = serviceCollection.BuildServiceProvider().GetService<TDatabaseConnectionSetup>();

        if (setup != null)
        {
            setupInstance = setup(setupInstance!);
        }

        SetupDatabase(token, setupInstance!);
        return new AltruistApplicationBuilder(Services, Settings, _args);
    }

    private void SetupDatabase<TDatabaseConnectionSetup>(IDatabaseServiceToken token, TDatabaseConnectionSetup instance) where TDatabaseConnectionSetup : class, IDatabaseConnectionSetup<TDatabaseConnectionSetup>
    {
        token.Configuration.Configure(Services);
        Services.AddSingleton<TDatabaseConnectionSetup>();
        instance.Build(Settings);
        // readding the built instance
        Services.AddSingleton(instance);
        Settings.DatabaseTokens.Add(token);
    }
}
