/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Text;

using Altruist.Contracts;
using Altruist.Engine;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist;

public enum ReadyState
{
    Starting = 0,
    Failed = 1,
    Alive = 2
}

[AppConfiguration(typeof(IServerStatus))]
public sealed class ServerStatus : IServerStatus, IAltruistConfiguration
{
    public ReadyState Status { get; private set; } = ReadyState.Starting;

    private readonly HashSet<IConnectable> _connectables = new();
    private readonly HashSet<IConnectable> _connected = new();
    private readonly ILogger<ServerStatus> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private bool _startup;
    private Timer? _startupTimeoutTimer;

    // All dependencies are injected; no manual BuildServiceProvider gymnastics.
    public ServerStatus(
        IGeneralDatabaseProvider dbProvider,
        ICacheProvider cacheProvider,
        IEnumerable<IConnectable> otherConnectables,
        ILoggerFactory loggerFactory,
        IHostApplicationLifetime lifetime)
    {
        _logger = loggerFactory.CreateLogger<ServerStatus>();
        _lifetime = lifetime;

        _connectables.Add(dbProvider);

        if (cacheProvider is IConnectable connectable)
            _connectables.Add(connectable);

        // User/feature supplied connectables
        foreach (var c in otherConnectables)
            _connectables.Add(c);
    }

    /// <summary>
    /// Kicks off the startup sequence (connect + wait + advertise readiness).
    /// Called automatically during startup via the [Configuration] mechanism.
    /// </summary>
    public async Task Configure(IServiceCollection services)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = services.BuildServiceProvider().GetRequiredService<IAltruistEngine>();

        if (_connectables.Count > 0)
            StartTimeoutTimer(engine);

        foreach (var service in _connectables)
        {
            if (service is IRelayService relay)
                _logger.LogInformation("🔗 Starting relay portal {RelayName}...", relay.ServiceName);

            SubscribeToServiceEvents(service, tcs, engine);

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

        await CheckAllConnectedAsync(tcs, engine);
    }

    public void SignalState(IAltruistEngine engine, ReadyState state)
    {
        if (state == ReadyState.Failed)
            engine.Stop();
        else if (state == ReadyState.Alive)
            engine.Start();

        Status = state;
    }

    private void StartTimeoutTimer(IAltruistEngine engine)
    {
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

    private void SubscribeToServiceEvents(IConnectable service, TaskCompletionSource<bool> tcs, IAltruistEngine engine)
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
                    _ = RunStartupActionsAsync(tcs, engine);
                }
                else if (_connected.Count == _connectables.Count && Status != ReadyState.Alive)
                {
                    _startupTimeoutTimer?.Dispose();
                    SignalState(engine, ReadyState.Alive);
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
            SignalState(engine, ReadyState.Failed);
            LogStatus();
        };

        service.OnRetryExhausted += ex =>
        {
            Shutdown(engine, $"{service.ServiceName} failed to connect after all retries.", ex);
        };
    }

    private async Task CheckAllConnectedAsync(TaskCompletionSource<bool> tcs, IAltruistEngine engine)
    {
        lock (_connected)
        {
            if (_connected.Count == _connectables.Count && !_startup)
            {
                _startup = true;
                _ = RunStartupActionsAsync(tcs, engine);
            }
        }

        await tcs.Task;
    }

    private async Task RunStartupActionsAsync(TaskCompletionSource<bool> tcs, IAltruistEngine engine)
    {
        _startupTimeoutTimer?.Dispose();

        SignalState(engine, ReadyState.Alive);
        _logger.LogInformation("🚀 Altruist is now live and ready to serve requests.");
        tcs.TrySetResult(true);

        await Task.CompletedTask;
    }

    private void Shutdown(IAltruistEngine engine, string reason, Exception? ex = null)
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

        SignalState(engine, ReadyState.Failed);
    }

    private void LogStatus() => Console.WriteLine(ToString());

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

public sealed class ReadinessMiddleware
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
