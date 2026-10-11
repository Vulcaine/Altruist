/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;

namespace Altruist.Gaming;

/// <summary>
/// Implement on world objects that support hibernation.
/// When no player can see this entity, it hibernates (removed from world, no AI/physics).
/// When a player moves close, it wakes up at its last known position.
/// </summary>
public interface IHibernatable
{
    /// <summary>Whether this entity is currently hibernated.</summary>
    bool IsHibernated { get; set; }

    /// <summary>Whether this entity supports hibernation. NPCs/buildings may not.</summary>
    bool CanHibernate { get; }

    /// <summary>Called before removing from world. Save any state needed for wake.</summary>
    void OnHibernate();

    /// <summary>Called after re-adding to world. Restore state.</summary>
    void OnWake();
}

/// <summary>
/// Stored state of a hibernated entity — enough to wake it later.
/// </summary>
public sealed class HibernatedEntity
{
    /// <summary>Instance id of the entity.</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>Zone it belongs to (for <see cref="IEntityHibernationService.RemoveZone"/>).</summary>
    public string ZoneName { get; init; } = "";
    /// <summary>Last known X (world units).</summary>
    public float X { get; init; }
    /// <summary>Last known Y (world units).</summary>
    public float Y { get; init; }
    /// <summary>Last known Z (world units).</summary>
    public float Z { get; init; }
    /// <summary>Template / prototype id (consumer-defined).</summary>
    public int Vnum { get; init; }
    /// <summary>Optional consumer state; not set by the service.</summary>
    public object? EntitySnapshot { get; set; }

    /// <summary>
    /// The actual entity object, kept alive in memory but removed from the world.
    /// This avoids re-creating from scratch — just re-insert into world on wake.
    /// </summary>
    public IHibernatable? Entity { get; set; }
}

/// <summary>
/// Manages entity hibernation. Entities with zero observers hibernate.
/// When a player moves, nearby hibernated entities wake up.
///
/// This is a core Altruist service that games can use for efficient entity management.
///
/// <para>The service is only a registry: removing the entity from the world on hibernate and re-inserting
/// it after <see cref="Wake"/> is the caller's job, as is deciding when (e.g. from visibility). While
/// hibernated, <see cref="IHibernatable.IsHibernated"/> makes the AI service skip the entity. Thread-safe.</para>
/// </summary>
public interface IEntityHibernationService
{
    /// <summary>Hibernate an entity. Removes from world but keeps in memory.</summary>
    void Hibernate(string instanceId, string zoneName, float x, float y, float z, int vnum, IHibernatable entity);

    /// <summary>Find hibernated entities near a position within radius.</summary>
    List<HibernatedEntity> FindNearby(float x, float y, float z, float radius);

    /// <summary>Wake a hibernated entity. Returns it for re-insertion into world.</summary>
    HibernatedEntity? Wake(string instanceId);

    /// <summary>Remove a hibernated entity permanently (zone deactivation).</summary>
    void Remove(string instanceId);

    /// <summary>Remove all hibernated entities for a zone.</summary>
    void RemoveZone(string zoneName);

    /// <summary>Total hibernated entities.</summary>
    int Count { get; }
}

/// <summary>Default <see cref="IEntityHibernationService"/> (registered when <c>altruist:game</c> exists).
/// <see cref="FindNearby"/> is a linear scan over all hibernated entities.</summary>
[Service(typeof(IEntityHibernationService))]
[ConditionalOnConfig("altruist:game")]
public sealed class EntityHibernationService : IEntityHibernationService
{
    private readonly ConcurrentDictionary<string, HibernatedEntity> _hibernated = new();

    /// <inheritdoc/>
    public int Count => _hibernated.Count;

    /// <summary>Marks the entity hibernated, calls <see cref="IHibernatable.OnHibernate"/> and stores it.</summary>
    public void Hibernate(string instanceId, string zoneName, float x, float y, float z, int vnum, IHibernatable entity)
    {
        entity.IsHibernated = true;
        entity.OnHibernate();

        _hibernated[instanceId] = new HibernatedEntity
        {
            InstanceId = instanceId,
            ZoneName = zoneName,
            X = x, Y = y, Z = z,
            Vnum = vnum,
            Entity = entity,
        };
    }

    /// <inheritdoc/>
    public List<HibernatedEntity> FindNearby(float x, float y, float z, float radius)
    {
        var radiusSq = radius * radius;
        var result = new List<HibernatedEntity>();

        foreach (var entry in _hibernated.Values)
        {
            var dx = entry.X - x;
            var dy = entry.Y - y;
            var dz = entry.Z - z;
            if (dx * dx + dy * dy + dz * dz <= radiusSq)
                result.Add(entry);
        }

        return result;
    }

    /// <summary>Removes the entry, clears <see cref="IHibernatable.IsHibernated"/> and calls
    /// <see cref="IHibernatable.OnWake"/>; null if not hibernated.</summary>
    public HibernatedEntity? Wake(string instanceId)
    {
        if (!_hibernated.TryRemove(instanceId, out var entry))
            return null;

        if (entry.Entity != null)
        {
            entry.Entity.IsHibernated = false;
            entry.Entity.OnWake();
        }

        return entry;
    }

    /// <inheritdoc/>
    public void Remove(string instanceId)
    {
        _hibernated.TryRemove(instanceId, out _);
    }

    /// <inheritdoc/>
    public void RemoveZone(string zoneName)
    {
        var toRemove = _hibernated.Where(kv => kv.Value.ZoneName == zoneName).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
            _hibernated.TryRemove(id, out _);
    }
}
