/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist.Dashboard;

/// <summary>
/// Publishes this server's <see cref="ArchitectureCardDto"/> to a shared fleet backplane with every fleet heartbeat
/// interval (it lives three intervals), so any server's dashboard can draw the whole fleet, and the other fleets on the
/// same backplane; removes it when the host stops. Does nothing with a backplane that is not shared (the dashboard then
/// takes the card itself).
/// </summary>
[Service(typeof(IHostedService))]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class ArchitectureCardPublisher : IHostedService
{
    /// <summary>Serializer options of a published card.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ArchitectureCardBuilder _builder;
    private readonly IFleet _fleet;
    private readonly IFleetBackplane _backplane;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>Created by DI.</summary>
    public ArchitectureCardPublisher(ArchitectureCardBuilder builder, IFleet fleet, IFleetBackplane backplane, ILoggerFactory loggerFactory)
    {
        _builder = builder;
        _fleet = fleet;
        _backplane = backplane;
        _logger = loggerFactory.CreateLogger<ArchitectureCardPublisher>();
        _interval = (fleet as IFleetInspection)?.Options.Heartbeat ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// The backplane key prefix of every card: <c>dashboard:card:{cluster}:{nodeId}</c>. It is not scoped to a cluster,
    /// so servers of independent fleets (channels) on one backplane see each other's architecture too.
    /// </summary>
    public const string KeyPrefix = "dashboard:card:";

    /// <summary>Starts publishing (first card immediately) when the backplane is shared.</summary>
    /// <param name="cancellationToken">Unused.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_backplane.Shared)
            return Task.CompletedTask;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await PublishAsync(token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
        return Task.CompletedTask;
    }

    /// <summary>Stops publishing and removes the card.</summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stop is null)
            return;
        _stop.Cancel();
        try
        { await _loop!.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        await _backplane.DeleteAsync(Key(), cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAsync(CancellationToken token)
    {
        try
        {
            var card = await _builder.BuildAsync().ConfigureAwait(false);
            await _backplane.SetAsync(Key(), JsonSerializer.Serialize(card, Json), _interval * 3, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backplane being away is a normal fleet state (each server serves alone): try again next time.
            _logger.LogWarning(ex, "Could not publish the dashboard architecture card; retrying in {Interval}.", _interval);
        }
    }

    private string Key() => $"{KeyPrefix}{_fleet.Cluster}:{_fleet.NodeId}";
}
