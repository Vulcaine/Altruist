/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Networking;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Automatically synchronizes all [Synchronized] ISynchronizedEntity world objects.
/// Ticked by GameWorldOrganizer3D — no game code needed.
/// Only broadcasts delta changes (via [Synced] properties).
/// </summary>
[Service(typeof(IEntitySyncService))]
[ConditionalOnConfig("altruist:game")]
public sealed class EntitySyncService : IEntitySyncService
{
    private readonly IClientSynchronizator? _synchronizer;
    private readonly ILogger _logger;
    private uint _tickCounter;

    public EntitySyncService(
        ILoggerFactory loggerFactory,
        IClientSynchronizator? synchronizer = null)
    {
        _synchronizer = synchronizer;
        _logger = loggerFactory.CreateLogger<EntitySyncService>();
    }

    /// <summary>
    /// Called each engine tick by the organizer. Receives worlds directly — no circular DI.
    /// </summary>
    public async Task Tick(IEnumerable<IGameWorldManager3D> worlds, float engineFrequencyHz)
    {
        if (_synchronizer == null) return;

        _tickCounter++;

        foreach (var world in worlds)
        {
            foreach (var obj in world.FindAllObjects<IWorldObject3D>())
            {
                if (obj is not ISynchronizedEntity syncEntity) continue;
                if (string.IsNullOrEmpty(syncEntity.ClientId)) continue;

                var syncAttr = obj.GetType().GetCustomAttributes(typeof(SynchronizedAttribute), true)
                    .FirstOrDefault() as SynchronizedAttribute;
                if (syncAttr == null) continue;

                if (syncAttr.Frequency > 0 && !ShouldSync(syncAttr, engineFrequencyHz))
                    continue;

                try
                {
                    await _synchronizer.SendAsync(syncEntity);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync entity {Id}", syncEntity.ClientId);
                }
            }
        }
    }

    private bool ShouldSync(SynchronizedAttribute attr, float engineHz)
    {
        uint interval = attr.Unit switch
        {
            SyncUnit.Ticks => (uint)attr.Frequency,
            SyncUnit.Hz => engineHz > 0 ? (uint)(engineHz / attr.Frequency) : 1,
            SyncUnit.Seconds => (uint)(engineHz * attr.Frequency),
            _ => 1,
        };

        return interval == 0 || (_tickCounter % interval) == 0;
    }
}

public interface IEntitySyncService
{
    Task Tick(IEnumerable<IGameWorldManager3D> worlds, float engineFrequencyHz);
}
