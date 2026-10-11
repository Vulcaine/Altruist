/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Text;

using Altruist.Engine;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>Server readiness as reported by <see cref="IServerStatus.Status"/>.</summary>
public enum ReadyState
{
    /// <summary>Startup in progress: waiting for connectable services (database, cache, ...) to connect. HTTP requests get 503.</summary>
    Starting = 0,
    /// <summary>A required service lost its connection (or startup failed); the engine is stopped and HTTP requests get 503.</summary>
    Failed = 1,
    /// <summary>All connectable services are connected; the engine runs and requests are served.</summary>
    Alive = 2
}

/// <summary>
/// Tracks readiness from the connection state of every <see cref="IConnectable"/> service (the database and cache
/// providers plus any registered <see cref="IConnectable"/>), and starts/stops the engine accordingly. Singleton;
/// inject <see cref="IServerStatus"/>.
/// </summary>
/// <remarks>
/// Goes <see cref="ReadyState.Alive"/> once all connectables are connected (immediately when there are none). A
/// connectable reporting failure switches to <see cref="ReadyState.Failed"/> (engine stopped) until it reconnects.
/// When an engine is present and not everything connects within 1 minute, or a service exhausts its retries, the
/// host is shut down via <see cref="IHostApplicationLifetime.StopApplication"/>.
/// </remarks>
[Service(typeof(IServerStatus))]
public sealed class ServerStatus : IServerStatus
{
    /// <inheritdoc/>
    public ReadyState Status { get; private set; } = ReadyState.Starting;

    private readonly HashSet<IConnectable> _connectables = new();
    private readonly HashSet<IConnectable> _connected = new();
    private readonly ILogger<ServerStatus> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private bool _startup;
    private Timer? _startupTimeoutTimer;

    /// <summary>The services whose connection state gates readiness.</summary>
    public HashSet<IConnectable> Connectables => _connectables;

    // All dependencies are injected; no manual BuildServiceProvider gymnastics.
    /// <summary>Collects the connectables to watch.</summary>
    /// <param name="otherConnectables">All services registered as <see cref="IConnectable"/>.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="lifetime">Used to stop the host when startup fails.</param>
    /// <param name="dbProvider">The database provider, watched when it is <see cref="IConnectable"/>.</param>
    /// <param name="cacheProvider">The cache provider, watched when it is <see cref="IConnectable"/>.</param>
    public ServerStatus(
        IEnumerable<IConnectable> otherConnectables,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime lifetime,
        IGeneralDatabaseProvider? dbProvider = null,
        ICacheProvider? cacheProvider = null)
    {
        _logger = loggerFactory.CreateLogger<ServerStatus>();
        _lifetime = lifetime;

        if (dbProvider is IConnectable conn1)
            _connectables.Add(conn1);

        if (cacheProvider is IConnectable conn2)
            _connectables.Add(conn2);

        // User/feature supplied connectables
        foreach (var c in otherConnectables)
            _connectables.Add(c);
    }

    /// <summary>
    /// Kicks off the startup sequence (connect + wait + advertise readiness).
    /// Called automatically after construction via <c>[PostConstruct]</c>; completes once the server is alive.
    /// </summary>
    /// <param name="engine">The engine to start when alive (null when no engine is configured).</param>
    /// <param name="token">Passed to the engine when it starts.</param>
    [PostConstruct]
    public async Task Configure(IEngineCore? engine = null, CancellationToken token = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_connectables.Count > 0)
            StartTimeoutTimer(engine);

        foreach (var service in _connectables)
        {
            SubscribeToServiceEvents(service, tcs, engine, token);

            if (service.IsConnected)
            {
                lock (_connected)
                    _connected.Add(service);
            }
            else
            {
                _ = service.ConnectAsync();
            }
        }

        await CheckAllConnectedAsync(tcs, engine, token);
    }

    /// <summary>
    /// The only place the engine is started (Alive) or stopped (Failed). The status is published
    /// before the engine starts, so its loop begins right away instead of waiting for it.
    /// </summary>
    public void SignalState(IEngineCore? engine, ReadyState state, CancellationToken token)
    {
        Status = state;

        if (state == ReadyState.Failed)
            engine?.Stop();
        else if (state == ReadyState.Alive)
            engine?.Start(token);
    }

    private void StartTimeoutTimer(IEngineCore? engine)
    {
        if (engine is null)
            return;

        _logger.LogInformation("⌛ Starting server timeout timer...");
        _startupTimeoutTimer?.Dispose();
        _startupTimeoutTimer = new Timer(_ =>
        {
            if (Status != ReadyState.Alive && !_startup)
            {
                Shutdown(engine, "❌ Startup timed out. Not all services connected in time.");
            }
        }, null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
    }

    private void SubscribeToServiceEvents(IConnectable service, TaskCompletionSource<bool> tcs, IEngineCore? engine, CancellationToken token)
    {
        service.OnConnected += () =>
        {
            lock (_connected)
            {
                if (_connected.Contains(service))
                    return;

                _logger.LogInformation("✅ {Service} is alive.", service.ServiceName);
                _connected.Add(service);

                if (_connected.Count == _connectables.Count && !_startup && Status != ReadyState.Alive)
                {
                    _startup = true;
                    _ = RunStartupActionsAsync(tcs, engine, token);
                }
                else if (_connected.Count == _connectables.Count && Status != ReadyState.Alive)
                {
                    _startupTimeoutTimer?.Dispose();
                    SignalState(engine, ReadyState.Alive, token);
                    _logger.LogInformation("🚀 Altruist is now live again and ready to serve requests.");
                }

                LogStatus();
            }
        };

        service.OnFailed += ex =>
        {
            _logger.LogError("❌ Lost connection to the service {Service}, reason: {Reason}", service.ServiceName, ex.Message);

            lock (_connected)
                _connected.Remove(service);

            StartTimeoutTimer(engine);
            SignalState(engine, ReadyState.Failed, token);
            LogStatus();
        };

        service.OnRetryExhausted += ex =>
        {
            Shutdown(engine, $"{service.ServiceName} failed to connect after all retries.", ex, token);
        };
    }

    private async Task CheckAllConnectedAsync(TaskCompletionSource<bool> tcs, IEngineCore? engine, CancellationToken token)
    {
        lock (_connected)
        {
            _logger.LogDebug("CheckAllConnected: {Connected}/{Total} connectables, startup={Startup}",
                _connected.Count, _connectables.Count, _startup);

            if (_connectables.Count == 0)
            {
                // No connectables at all — go alive immediately
                _startup = true;
                _ = RunStartupActionsAsync(tcs, engine, token);
            }
            else if (_connected.Count == _connectables.Count && !_startup)
            {
                _startup = true;
                _ = RunStartupActionsAsync(tcs, engine, token);
            }
        }

        await tcs.Task;
    }

    private async Task RunStartupActionsAsync(TaskCompletionSource<bool> tcs, IEngineCore? engine, CancellationToken token)
    {
        _startupTimeoutTimer?.Dispose();

        SignalState(engine, ReadyState.Alive, token);
        _logger.LogInformation("🚀 Altruist is now live and ready to serve requests.");
        tcs.TrySetResult(true);

        await Task.CompletedTask;
    }

    private void Shutdown(IEngineCore? engine, string reason, Exception? ex = null, CancellationToken token = default)
    {
        if (ex is not null)
            _logger.LogCritical(ex, "{Reason}", reason);
        else
            _logger.LogCritical("{Reason}", reason);

        // Gracefully stop the host; last resort could be Environment.Exit(1)
        if (_lifetime != null)
        {
            _lifetime.StopApplication();
        }
        else
        {
            Environment.Exit(1);
        }

        SignalState(engine, ReadyState.Failed, token);
    }

    private void LogStatus() => Console.WriteLine(ToString());

    /// <summary>A console table of each connectable and whether it is connected; empty when there are none.</summary>
    public override string ToString()
    {
        if (_connectables.Count == 0)
            return string.Empty;

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

/// <summary>
/// ASP.NET Core middleware that answers 503 ("Service is not ready.") to every request that reaches it while
/// <see cref="IServerStatus.Status"/> is not <see cref="ReadyState.Alive"/>. Added to the pipeline by the framework.
/// </summary>
public sealed class ReadinessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IServerStatus _appStatus;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="appStatus">The readiness source.</param>
    public ReadinessMiddleware(RequestDelegate next, IServerStatus appStatus)
    {
        _next = next;
        _appStatus = appStatus;
    }

    /// <summary>Short-circuits with 503 unless the server is alive; otherwise calls the next middleware.</summary>
    /// <param name="context">The HTTP context.</param>
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
