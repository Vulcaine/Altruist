/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text;
using Altruist.Engine;
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

public class ServerStatus : IServerStatus
{
    public ReadyState Status { get; private set; } = ReadyState.Starting;

    private readonly IServiceProvider _serviceProvider;
    private readonly HashSet<IConnectable> _connectables = new();
    private readonly HashSet<IConnectable> _connected = new();
    private bool _startup;
    private Timer? _startupTimeoutTimer;

    public ServerStatus(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

        var context = serviceProvider.GetRequiredService<IAltruistContext>();
        var dbProviders = serviceProvider.GetServices<IGeneralDatabaseProvider>();
        var cacheProviders = serviceProvider.GetServices<ICacheProvider>();
        var relayServices = serviceProvider.GetServices<IRelayService>();
        var allOtherConnectibles = serviceProvider.GetServices<IConnectable>().ToList();

        foreach (var dbToken in context.DatabaseTokens)
        {
            var db = dbProviders.FirstOrDefault(s => s.Token == dbToken)
                ?? throw new InvalidOperationException($"❌ Database service with token `{dbToken}` not found.");
            _connectables.Add(db);
        }

        if (context.CacheToken is { } cacheToken)
        {
            var cache = cacheProviders.FirstOrDefault(c => c.Token == cacheToken)
                ?? throw new InvalidOperationException($"❌ Cache service with token `{cacheToken}` not found.");
            if (cache is IConnectable connectable)
            {
                _connectables.Add(connectable);
            }
        }

        foreach (var connectable in allOtherConnectibles)
        {
            _connectables.Add(connectable);
        }

        foreach (var relayService in relayServices)
        {
            _connectables.Add(relayService);
        }
    }

    public void SignalState(ReadyState state)
    {
        var engine = _serviceProvider.GetService<IAltruistEngine>();

        if (engine != null)
        {
            if (state == ReadyState.Failed)
                engine.Stop();
            else if (state == ReadyState.Alive)
                engine.Start();
        }

        Status = state;
    }

    public async Task StartupAsync(AppManager manager)
    {
        var app = manager.App;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<AppManager>();
        var actions = app.Services.GetServices<IAction>();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_connectables.Count > 0)
        {
            StartTimeoutTimer(manager, logger);
        }

        foreach (var service in _connectables)
        {
            if (service is IRelayService relay)
            {
                logger.LogInformation($"🔗 Starting relay portal {relay.ServiceName}...");
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

        await CheckAllConnectedAsync(actions, logger, manager.App.Services, tcs);
    }

    private void StartTimeoutTimer(AppManager manager, ILogger logger)
    {
        logger.LogInformation("⌛ Starting server timeout timer...");
        _startupTimeoutTimer?.Dispose();
        _startupTimeoutTimer = new Timer(_ =>
        {
            if (Status != ReadyState.Alive && !_startup)
            {
                manager.Shutdown(null, "❌ Startup timed out. Not all services connected in time.");
            }
        }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
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
                if (_connected.Contains(service))
                {
                    return;
                }

                logger.LogInformation($"✅ {service.ServiceName} is alive.");
                _connected.Add(service);

                if (_connected.Count == _connectables.Count && !_startup && Status != ReadyState.Alive)
                {
                    _startup = true;
                    _ = RunStartupActionsAsync(actions, logger, manager.App.Services, tcs);
                }
                else if (_connected.Count == _connectables.Count && Status != ReadyState.Alive)
                {
                    _startupTimeoutTimer?.Dispose();
                    SignalState(ReadyState.Alive);
                    logger.LogInformation("🚀 Altruist is now live again and ready to serve requests.");
                }

                LogStatus();
            }
        };

        service.OnFailed += ex =>
        {
            logger.LogError($"❌ Lost connection to the service {service.ServiceName}, reason: {ex.Message}");

            lock (_connected)
            {
                _connected.Remove(service);
            }

            StartTimeoutTimer(manager, logger);
            SignalState(ReadyState.Failed);
            LogStatus();
        };

        service.OnRetryExhausted += ex =>
        {
            manager.Shutdown(ex, $"{service.ServiceName} failed to connect after all retries.");
        };
    }

    private async Task CheckAllConnectedAsync(
        IEnumerable<IAction> actions,
        ILogger logger,
        IServiceProvider serviceProvider,
        TaskCompletionSource<bool> tcs)
    {
        lock (_connected)
        {
            if (_connected.Count == _connectables.Count && !_startup)
            {
                _startup = true;
                _ = RunStartupActionsAsync(actions, logger, serviceProvider, tcs);
            }
        }

        await tcs.Task;
    }

    private async Task RunStartupActionsAsync(
        IEnumerable<IAction> actions,
        ILogger logger,
        IServiceProvider serviceProvider,
        TaskCompletionSource<bool> tcs)
    {
        _startupTimeoutTimer?.Dispose();
        logger.LogInformation("✅ All required services connected. Running startup actions...");
        foreach (var action in actions)
            await action.Run(serviceProvider);

        SignalState(ReadyState.Alive);
        logger.LogInformation("🚀 Altruist is now live and ready to serve requests.");
        tcs.TrySetResult(true);
    }

    private void LogStatus()
    {
        Console.WriteLine(ToString());
    }

    public override string ToString()
    {
        if (_connectables.Count == 0)
        {
            return string.Empty;
        }

        const int nameColWidth = 24;
        const int statusColWidth = 16;
        var sb = new StringBuilder();

        var topBorder = $"╔{new string('═', nameColWidth)}╦{new string('═', statusColWidth)}╗";
        var header = $"║{"Service Name".PadRight(nameColWidth)}║{"Status".PadRight(statusColWidth)}║";
        var separator = $"╠{new string('═', nameColWidth)}╬{new string('═', statusColWidth)}╣";
        var bottomBorder = $"╚{new string('═', nameColWidth)}╩{new string('═', statusColWidth)}╝";

        sb.AppendLine();
        sb.AppendLine("🔌 Service Connection Status");
        sb.AppendLine(topBorder);
        sb.AppendLine(header);
        sb.AppendLine(separator);

        foreach (var service in _connectables.OrderBy(s => s.ServiceName))
        {
            var status = service.IsConnected ? "✅ Connected" : "❌ Disconnected";
            sb.AppendLine($"║{service.ServiceName.PadRight(nameColWidth)}║{status.PadRight(statusColWidth - 1)}║");
        }

        sb.AppendLine(bottomBorder);

        return sb.ToString();
    }

}


public class ReadinessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServerStatus _appStatus;

    public ReadinessMiddleware(RequestDelegate next, IServerStatus appStatus)
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
