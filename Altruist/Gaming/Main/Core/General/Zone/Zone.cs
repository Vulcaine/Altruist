/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming;

/// <summary>
/// A spawn definition within a zone. Game-agnostic — holds the data needed
/// to spawn an entity. The actual entity creation is done by the game via
/// IZoneSpawnHandler.
/// </summary>
/// <remarks>The framework only stores these values and hands them to the <see cref="IZoneSpawnHandler"/>;
/// their interpretation (units, what a type or template id means) is up to the game.</remarks>
public class ZoneSpawnDefinition
{
    /// <summary>Consumer-defined spawn kind (e.g. a single entity or a group).</summary>
    public string Type { get; set; } = "";
    /// <summary>Spawn X in world units.</summary>
    public int X { get; set; }
    /// <summary>Spawn Y in world units.</summary>
    public int Y { get; set; }
    /// <summary>Spawn Z in world units.</summary>
    public int Z { get; set; }
    /// <summary>Random spread around <see cref="X"/>.</summary>
    public int RangeX { get; set; }
    /// <summary>Random spread around <see cref="Y"/>.</summary>
    public int RangeY { get; set; }
    /// <summary>Initial facing (consumer-defined encoding).</summary>
    public int Direction { get; set; }
    /// <summary>How many entities to spawn (default 1).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Template / prototype id of the entity to spawn.</summary>
    public int Vnum { get; set; }
    /// <summary>Respawn delay in seconds (default 60).</summary>
    public double RegenSeconds { get; set; } = 60;
    /// <summary>Respawn percentage (default 100).</summary>
    public int RegenPercent { get; set; } = 100;
}

/// <summary>
/// Represents a named zone (map region) that can be activated/deactivated.
/// When active, its spawn definitions are materialized into world objects.
/// When inactive, all spawned objects are removed to save resources.
/// </summary>
public interface IManagedZone : IZone
{
    /// <summary>Players currently inside.</summary>
    int PlayerCount { get; }
    /// <summary>Spawns materialised when the zone activates.</summary>
    IReadOnlyList<ZoneSpawnDefinition> SpawnDefinitions { get; }
    /// <summary>Instance ids returned by the last <see cref="IZoneSpawnHandler.SpawnZone"/> (a copy).</summary>
    IReadOnlyCollection<string> SpawnedInstanceIds { get; }
}

/// <summary>
/// Callback interface for the game to implement entity spawning/despawning.
/// Altruist calls these when zones activate/deactivate.
/// </summary>
public interface IZoneSpawnHandler
{
    /// <summary>Spawn entities for this zone. Return instance IDs of spawned objects.</summary>
    Task<List<string>> SpawnZone(IManagedZone zone);

    /// <summary>Despawn all entities for this zone.</summary>
    Task DespawnZone(IManagedZone zone, IReadOnlyCollection<string> instanceIds);
}

/// <summary>
/// Manages zone lifecycle. Zones activate when the first player enters
/// and deactivate when the last player leaves.
///
/// <para>Choosing: use this for presence-driven spawning in persistent worlds (content exists only
/// while someone is there). For static spatial regions with bounds checks use
/// <see cref="IZoneManager2D"/> / <see cref="IZoneManager3D"/>; to park individual idle entities use
/// <see cref="IEntityHibernationService"/>. The game reports enters/leaves; nothing calls these
/// automatically.</para>
/// </summary>
public interface IZoneManager
{
    /// <summary>Register a zone with its spawn definitions.</summary>
    void RegisterZone(string name, List<ZoneSpawnDefinition> spawns);

    /// <summary>Called when a player enters a zone. Activates the zone if first player.</summary>
    Task PlayerEnteredZone(string zoneName, string playerId);

    /// <summary>Called when a player leaves a zone. Deactivates the zone if last player.</summary>
    Task PlayerLeftZone(string zoneName, string playerId);

    /// <summary>Get zone by name.</summary>
    IManagedZone? GetZone(string name);

    /// <summary>Get all registered zone names.</summary>
    IEnumerable<string> GetAllZoneNames();

    /// <summary>Get all currently active zones.</summary>
    IEnumerable<IManagedZone> GetActiveZones();

    /// <summary>Total spawned entities across all active zones.</summary>
    int TotalSpawnedEntities { get; }
}

/// <summary>
/// Default <see cref="IZoneManager"/> (registered when <c>altruist:game</c> exists). Thread-safe. Spawning on
/// activation is fire-and-forget (the enter call returns before the entities exist; a failed spawn
/// resets the zone to inactive so the next enter retries); despawning on deactivation is awaited.
/// Without an <see cref="IZoneSpawnHandler"/> registered it only tracks presence.
/// </summary>
[Service(typeof(IZoneManager))]
[ConditionalOnConfig("altruist:game")]
public sealed class ZoneManager : IZoneManager
{
    private readonly ConcurrentDictionary<string, Zone> _zones = new();
    private readonly IZoneSpawnHandler? _spawnHandler;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public int TotalSpawnedEntities => _zones.Values.Where(z => z.IsActive).Sum(z => z.SpawnedIds.Count);

    /// <summary>Created by DI; <paramref name="spawnHandler"/> is the game's optional spawn callback.</summary>
    public ZoneManager(IZoneSpawnHandler? spawnHandler = null)
    {
        _spawnHandler = spawnHandler;
    }

    /// <summary>Registers (or replaces, resetting presence) a zone.</summary>
    public void RegisterZone(string name, List<ZoneSpawnDefinition> spawns)
    {
        _zones[name] = new Zone(name, spawns);
    }

    /// <inheritdoc/>
    public Task PlayerEnteredZone(string zoneName, string playerId)
    {
        if (!_zones.TryGetValue(zoneName, out var zone)) return Task.CompletedTask;

        bool shouldActivate;
        lock (_lock)
        {
            zone.Players.Add(playerId);
            shouldActivate = !zone.IsActive && zone.Players.Count == 1;
            if (shouldActivate) zone.IsActive = true;
        }

        // Fire-and-forget spawn. PlayerEnteredZone returns immediately so the
        // calling enter-game flow doesn't block on (potentially hundreds of)
        // entity instantiations — the dominant cost in real workloads. The
        // visibility tracker picks the entities up on its next tick as they
        // come online, so the player sees mobs trickle in over a frame or two
        // instead of a 300ms+ stall before they can move at all.
        //
        // The IsActive=true flip happens inside the lock above, so a second
        // player entering during the spawn race correctly sees shouldActivate=false
        // and does not double-spawn.
        if (shouldActivate && _spawnHandler != null)
        {
            _ = SpawnZoneInBackgroundAsync(zone);
        }

        return Task.CompletedTask;
    }

    private async Task SpawnZoneInBackgroundAsync(Zone zone)
    {
        try
        {
            var ids = await _spawnHandler!.SpawnZone(zone);
            zone.SpawnedIds = new ConcurrentBag<string>(ids);
        }
        catch (Exception)
        {
            // Reset IsActive so a future PlayerEnteredZone for the same zone
            // can retry the spawn instead of getting stuck "active with zero mobs".
            zone.IsActive = false;
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task PlayerLeftZone(string zoneName, string playerId)
    {
        if (!_zones.TryGetValue(zoneName, out var zone)) return;

        bool shouldDeactivate;
        lock (_lock)
        {
            zone.Players.Remove(playerId);
            shouldDeactivate = zone.IsActive && zone.Players.Count == 0;
            if (shouldDeactivate) zone.IsActive = false;
        }

        if (shouldDeactivate && _spawnHandler != null)
        {
            var ids = zone.SpawnedIds.ToList();
            zone.SpawnedIds = new ConcurrentBag<string>();
            await _spawnHandler.DespawnZone(zone, ids);
        }
    }

    /// <inheritdoc/>
    public IManagedZone? GetZone(string name) => _zones.GetValueOrDefault(name);

    /// <inheritdoc/>
    public IEnumerable<string> GetAllZoneNames() => _zones.Keys;

    /// <inheritdoc/>
    public IEnumerable<IManagedZone> GetActiveZones() => _zones.Values.Where(z => z.IsActive);

    private sealed class Zone : IManagedZone
    {
        public string Name { get; }
        public bool IsActive { get; set; }
        public int PlayerCount => Players.Count;
        public IReadOnlyList<ZoneSpawnDefinition> SpawnDefinitions { get; }
        public IReadOnlyCollection<string> SpawnedInstanceIds => SpawnedIds.ToList();

        public HashSet<string> Players { get; } = new();
        public ConcurrentBag<string> SpawnedIds { get; set; } = new();

        public Zone(string name, List<ZoneSpawnDefinition> spawns)
        {
            Name = name;
            SpawnDefinitions = spawns;
        }
    }
}
