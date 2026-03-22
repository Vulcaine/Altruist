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
public class ZoneSpawnDefinition
{
    public string Type { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int RangeX { get; set; }
    public int RangeY { get; set; }
    public int Direction { get; set; }
    public int Count { get; set; } = 1;
    public int Vnum { get; set; }
    public double RegenSeconds { get; set; } = 60;
    public int RegenPercent { get; set; } = 100;
}

/// <summary>
/// Represents a named zone (map region) that can be activated/deactivated.
/// When active, its spawn definitions are materialized into world objects.
/// When inactive, all spawned objects are removed to save resources.
/// </summary>
public interface IManagedZone : IZone
{
    int PlayerCount { get; }
    IReadOnlyList<ZoneSpawnDefinition> SpawnDefinitions { get; }
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

[Service(typeof(IZoneManager))]
[ConditionalOnConfig("altruist:game")]
public sealed class ZoneManager : IZoneManager
{
    private readonly ConcurrentDictionary<string, Zone> _zones = new();
    private readonly IZoneSpawnHandler? _spawnHandler;
    private readonly object _lock = new();

    public int TotalSpawnedEntities => _zones.Values.Where(z => z.IsActive).Sum(z => z.SpawnedIds.Count);

    public ZoneManager(IZoneSpawnHandler? spawnHandler = null)
    {
        _spawnHandler = spawnHandler;
    }

    public void RegisterZone(string name, List<ZoneSpawnDefinition> spawns)
    {
        _zones[name] = new Zone(name, spawns);
    }

    public async Task PlayerEnteredZone(string zoneName, string playerId)
    {
        if (!_zones.TryGetValue(zoneName, out var zone)) return;

        bool shouldActivate;
        lock (_lock)
        {
            zone.Players.Add(playerId);
            shouldActivate = !zone.IsActive && zone.Players.Count == 1;
            if (shouldActivate) zone.IsActive = true;
        }

        if (shouldActivate && _spawnHandler != null)
        {
            var ids = await _spawnHandler.SpawnZone(zone);
            zone.SpawnedIds = new ConcurrentBag<string>(ids);
        }
    }

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

    public IManagedZone? GetZone(string name) => _zones.GetValueOrDefault(name);

    public IEnumerable<string> GetAllZoneNames() => _zones.Keys;

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
