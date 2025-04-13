using Altruist.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public interface IAction
{
    Task Run();
}

public abstract class ActionBase : IAction
{
    protected IServiceProvider ServiceProvider { get; private set; } = default!;
    protected ILogger Logger { get; private set; } = default!;

    public ActionBase(IServiceProvider serviceProvider)
    {
        ServiceProvider = serviceProvider;
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        Logger = loggerFactory.CreateLogger(GetType());
    }

    public abstract Task Run();
}

public class StartupActions
{
    public static readonly List<IAction> Actions = new();

    public static void Add(IAction action) => Actions.Add(action);
}

// TODO: we must attempt to do this on Cache/DB reconnect as well if its not already done
public class LoadSyncServicesAction : ActionBase
{
    public LoadSyncServicesAction(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        StartupActions.Add(this);
    }

    public override async Task Run()
    {
        var syncServices = ServiceProvider.GetServices<object>()
            .Where(s => s?.GetType().GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVaultCacheSyncService<>)) == true)
            .ToList();

        if (syncServices.Count > 0)
        {
            Logger.LogInformation($"🔄 Loading {syncServices.Count} vault cache sync services.");
        }

        bool error = false;

        foreach (var service in syncServices)
        {
            try
            {
                var syncInterface = service.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IVaultCacheSyncService<>));

                if (syncInterface != null)
                {
                    var loadMethod = syncInterface.GetMethod("Load");
                    if (loadMethod != null)
                    {
                        Logger.LogInformation($"🔂 Populating cache using {service.GetType().Name}.");
                        var task = (Task)loadMethod.Invoke(service, null)!;
                        await task;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"❌ Error while loading cache from {service.GetType().Name}. Will retry when resolved.");
                error = true;
                break;
            }
        }

        if (syncServices.Count > 0 && !error)
        {
            Logger.LogInformation($"✅ Cache sync complete.");
        }
    }
}


public static class StartupActionsExtensions
{
    public static async Task LoadSyncServices(this StartupActions _, IServiceProvider serviceProvider)
    {
        await new LoadSyncServicesAction(serviceProvider).Run();
    }
}
