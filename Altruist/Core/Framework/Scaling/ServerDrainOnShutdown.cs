/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>
/// Drains the <see cref="IServerNode"/> when the host stops (SIGTERM, Ctrl+C, a Kubernetes pod
/// termination): its <see cref="StoppingAsync"/> runs before any hosted service stops, so the
/// engine keeps stepping the running work until it finished or the drain timed out.
/// Config <c>altruist:server:drain:on-shutdown</c> (true). The host's shutdown timeout is raised
/// to cover <c>drain:timeout</c> + <c>drain:force-grace</c>; keep the orchestrator's grace period
/// (Kubernetes <c>terminationGracePeriodSeconds</c>) above it.
/// </summary>
[Service(typeof(IHostedService))]
public sealed class ServerDrainOnShutdown : IHostedLifecycleService
{
    private readonly IServerNode _node;
    private readonly bool _enabled;
    private readonly ILogger _logger;

    public ServerDrainOnShutdown(
        IServerNode node,
        [AppConfigValue("altruist:server:drain:on-shutdown", "true")] bool enabled = true,
        ILoggerFactory? loggerFactory = null)
    {
        _node = node;
        _enabled = enabled;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ServerDrainOnShutdown>();
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (!_enabled) return;
        try
        {
            var inTime = await _node.DrainAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!inTime)
                _logger.LogWarning("Shutdown drain of {Node} timed out; the remaining work was stopped.", _node.NodeId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutdown drain of {Node} was cut short by the host's shutdown timeout.", _node.NodeId);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
