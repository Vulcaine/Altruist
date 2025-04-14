using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist;

public enum ReadyState
{
    Starting = 0,
    Failed = 1,
    Alive = 2
}

public class AppStatus : IAppStatus
{
    public ReadyState Status { get; private set; } = ReadyState.Starting;

    private readonly IServiceProvider _serviceProvider;
    private readonly List<IConnectable> _connectables = new();
    private readonly HashSet<IConnectable> _connected = new();

    public AppStatus(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        var context = serviceProvider.GetRequiredService<IAltruistContext>();
        var dbProviders = serviceProvider.GetServices<IGeneralDatabaseProvider>();
        var cacheProviders = serviceProvider.GetServices<ICacheProvider>();
        var relayServices = serviceProvider.GetServices<IRelayService>();

        foreach (var dbToken in context.DatabaseTokens)
        {
            var db = dbProviders.FirstOrDefault(s => s.Token == dbToken);
            if (db == null)
            {
                throw new InvalidOperationException($"❌ Database service with token `{dbToken}` not found.");
            }
            _connectables.Add(db);
        }

        if (context.CacheToken is { } cacheToken)
        {
            var cache = cacheProviders.FirstOrDefault(c => c.Token == cacheToken);
            if (cache == null)
            {
                throw new InvalidOperationException($"❌ Cache service with token `{cacheToken}` not found.");
            }
            else if (cache is IConnectable connectable)
            {
                _connectables.Add(connectable);
            }
        }

        _connectables.AddRange(relayServices);
    }

    public void SignalState(ReadyState state)
    {
        var engine = _serviceProvider.GetService<IAltruistEngine>();

        if (engine != null && state == ReadyState.Failed)
            engine.Stop();
        else if (engine != null && state == ReadyState.Alive)
            engine.Start();

        Status = state;
    }

    public async Task StartupAsync(AppManager manager)
    {
        var app = manager.App;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AppManager>();
        var actions = app.Services.GetServices<IAction>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var timer = new Timer(_ =>
        {
            if (Status != ReadyState.Alive)
            {
                manager.Shutdown(null, "❌ Startup timed out. Not all services connected in time.");
            }
        }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);

        foreach (var service in _connectables)
        {
            if (service is IRelayService relay)
            {
                logger.LogInformation($"🔗 Starting relay portal {relay.GetType().Name}...");
            }

            SubscribeToServiceEvents(service, manager, actions, tcs, logger);

            if (service.IsConnected)
            {
                lock (_connected)
                {
                    _connected.Add(service);
                }
            }
            else
            {
                _ = service.ConnectAsync();
            }
        }

        await CheckAllConnectedAsync(actions, logger, tcs);
    }

    private void SubscribeToServiceEvents(
        IConnectable service,
        AppManager manager,
        IEnumerable<IAction> actions,
        TaskCompletionSource<bool> tcs,
        ILogger logger)
    {
        service.OnConnected += () =>
        {
            lock (_connected)
            {
                _connected.Add(service);

                if (_connected.Count == _connectables.Count && Status == ReadyState.Starting)
                {
                    _ = RunStartupActionsAsync(actions, logger, tcs);
                }
            }
        };

        service.OnFailed += ex =>
        {
            lock (_connected)
            {
                _connected.Remove(service);
            }

            SignalState(ReadyState.Failed);
        };

        service.OnRetryExhausted += ex =>
        {
            manager.Shutdown(ex, $"❌ {service.GetType()} failed to connect after all retries.");
        };
    }

    private async Task CheckAllConnectedAsync(
        IEnumerable<IAction> actions,
        ILogger logger,
        TaskCompletionSource<bool> tcs)
    {
        lock (_connected)
        {
            if (_connected.Count == _connectables.Count && Status == ReadyState.Starting)
            {
                _ = RunStartupActionsAsync(actions, logger, tcs);
            }
        }

        await tcs.Task;
    }

    private async Task RunStartupActionsAsync(
        IEnumerable<IAction> actions,
        ILogger logger,
        TaskCompletionSource<bool> tcs)
    {
        logger.LogInformation("✅ All required services connected. Running startup actions...");
        foreach (var action in actions)
            await action.Run();

        SignalState(ReadyState.Alive);
        logger.LogInformation("🚀 Altruist is now live and ready to serve requests.");
        tcs.TrySetResult(true);
    }
}


public class ReadinessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAppStatus _appStatus;

    public ReadinessMiddleware(RequestDelegate next, IAppStatus appStatus)
    {
        _next = next;
        _appStatus = appStatus;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_appStatus.Status != ReadyState.Alive)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Service is not ready.");
            return;
        }

        await _next(context);
    }
}
