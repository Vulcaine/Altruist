using System.Reflection;
using Altruist.Codec;
using Altruist.Database;
using Altruist.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;

namespace Altruist;

public class AltruistBuilder
{
    private IServiceCollection Services { get; } = new ServiceCollection();
    private string[] _args;
    public IAltruistContext Settings { get; } = new AltruistServerContext();

    private AltruistBuilder(string[] args, Func<IServiceCollection, IServiceCollection>? serviceBuilder = null)
    {
        Services = serviceBuilder != null ? serviceBuilder.Invoke(new ServiceCollection()) : new ServiceCollection();
        _args = args;
        Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            var frameworkVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";

            loggingBuilder.AddProvider(new AltruistLoggerProvider(frameworkVersion));
        });
        // Add core services
        Services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "Altruist",
            ContentRootPath = Directory.GetCurrentDirectory()
        });
        Services.AddSingleton(Services);
        Services.AddSingleton(Settings);
        Services.AddSingleton<ClientSender>();

        Services.AddSingleton<RoomSender>();
        Services.AddSingleton<BroadcastSender>();
        Services.AddSingleton<ClientSynchronizator>();
        // Setup cache
        Services.AddSingleton<InMemoryCache>();
        Services.AddSingleton<IMemoryCacheProvider>(sp => sp.GetRequiredService<InMemoryCache>());
        Services.AddSingleton<ICacheProvider>(sp => sp.GetRequiredService<InMemoryCache>());

        Services.AddSingleton<IAltruistRouter, InMemoryDirectRouter>();
        Services.AddSingleton<ICodec, JsonCodec>();
        Services.AddSingleton<IDecoder, JsonMessageDecoder>();
        Services.AddSingleton<IEncoder, JsonMessageEncoder>();
        Services.AddSingleton<IConnectionStore, InMemoryConnectionStore>();

        Services.AddSingleton<IPortalContext, PortalContext>();
        Services.AddSingleton<VaultRepositoryFactory>();
        Services.AddSingleton<DatabaseProviderFactory>();
        Services.AddSingleton(sp => new LoadSyncServicesAction(sp));
        Services.AddSingleton<IAction>(sp => sp.GetRequiredService<LoadSyncServicesAction>());
        Services.AddSingleton<IServerStatus>(sp => new ServerStatus(sp));
    }

    public static AltruistIntermediateBuilder Create(string[] args, Func<IServiceCollection, IServiceCollection>? serviceBuilder = null) => new AltruistBuilder(args, serviceBuilder).ToConnectionBuilder();

    private AltruistIntermediateBuilder ToConnectionBuilder() => new AltruistIntermediateBuilder(Services, Settings, _args);
}

public class AltruistIntermediateBuilder
{
    public IServiceCollection Services { get; }
    public IAltruistContext Settings;
    public string[] Args;

    public AltruistIntermediateBuilder(IServiceCollection services, IAltruistContext Settings, string[] args)
    {
        Services = services;
        this.Settings = Settings;
        Args = args;
    }

    public AltruistConnectionBuilder NoEngine()
    {
        return new AltruistConnectionBuilder(Services, Settings, Args);
    }
}