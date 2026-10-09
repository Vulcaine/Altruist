/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.TwoD
{
    /// <summary>A uniform-grid spatial hash of <see cref="IWorldObject2D"/>s, used inside each
    /// <see cref="WorldPartition2D"/> for radius queries by archetype and zone. Objects are filed by their
    /// <c>Transform.Position</c> at the time of <see cref="Add"/>; when an object moves, remove and re-add it
    /// (the world manager's <c>UpdateObjectPosition</c> does this). Public dictionaries use string keys so
    /// the index is JSON-serializable. Not thread-safe.
    /// <para>Prefer the world-level queries on <see cref="IGameWorldManager2D"/>; use this type directly
    /// only for a custom partition or a standalone spatial lookup.</para></summary>
    public class SpatialGridIndex2D
    {
        /// <summary>Cell edge length in world units (cell = <c>position / CellSize</c>, integer division).</summary>
        public int CellSize { get; set; }

        // Use stringified keys like "x:y" to allow JSON serialization
        // Flatten all objects by instance id
        /// <summary>All filed objects by instance id.</summary>
        public Dictionary<string, IWorldObject2D> InstanceMap { get; set; } = new();

        // grid key => set of instance ids
        /// <summary>Cell key (<c>"cx:cy"</c>) to the instance ids in that cell.</summary>
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional, used for type filtering, archetype string => set of instance ids
        /// <summary>Archetype (<c>""</c> when none) to the instance ids with it.</summary>
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        /// <summary>Parameterless constructor for deserialization; set <see cref="CellSize"/> before use.</summary>
        public SpatialGridIndex2D() { }

        /// <summary>Creates an empty index with the given cell size.</summary>
        /// <param name="cellSize">Cell edge length in world units (must be positive).</param>
        public SpatialGridIndex2D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y) => $"{x}:{y}";

        // Cells are [k * CellSize, (k + 1) * CellSize): floor, so negative coordinates get their own cells.
        private string CellKeyOf(IWorldObject2D obj) => GetKey(
            (int)MathF.Floor(obj.Transform.Position.X / CellSize),
            (int)MathF.Floor(obj.Transform.Position.Y / CellSize));

        /// <summary>Files <paramref name="obj"/> under its current position cell and archetype (re-adding
        /// an id overwrites the instance but does not remove the old cell entry).</summary>
        public virtual void Add(IWorldObject2D obj)
        {
            string key = CellKeyOf(obj);

            if (!Grid.TryGetValue(key, out var list))
                Grid[key] = list = new HashSet<string>();

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var typeKey = obj.ObjectArchetype;
            if (!TypeMap.TryGetValue(typeKey ?? "", out var typeSet))
                TypeMap[typeKey ?? ""] = typeSet = new HashSet<string>();

            typeSet.Add(obj.InstanceId);
        }

        /// <summary>Removes the object, looking up its cell from its <em>current</em> position.</summary>
        /// <returns>The removed object, or null when not indexed.</returns>
        public virtual IWorldObject2D? Remove(string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj))
                return null;

            string key = CellKeyOf(obj);

            if (Grid.TryGetValue(key, out var list))
            {
                list.Remove(instanceId);
            }

            if (TypeMap.TryGetValue(obj.ObjectArchetype ?? "", out var map))
            {
                map.Remove(instanceId);
            }

            InstanceMap.Remove(instanceId);
            return obj;
        }

        /// <summary>Objects whose archetype equals <paramref name="archetype"/>, whose <c>ZoneId</c> equals
        /// <paramref name="zoneId"/> and whose position is within <paramref name="radius"/> of
        /// (<paramref name="x"/>, <paramref name="y"/>) (inclusive). Scans the cells overlapping the
        /// query square.</summary>
        /// <param name="archetype">Archetype to match (exact).</param>
        /// <param name="x">Center X, world units.</param>
        /// <param name="y">Center Y, world units.</param>
        /// <param name="radius">Search radius, world units.</param>
        /// <param name="zoneId">Zone id to match (exact; <c>""</c> for no zone).</param>
        public virtual IEnumerable<IWorldObject2D> Query(
            string archetype,
            int x, int y,
            float radius,
            string zoneId)
        {
            int minX = (int)MathF.Floor((x - radius) / CellSize);
            int maxX = (int)MathF.Floor((x + radius) / CellSize);
            int minY = (int)MathF.Floor((y - radius) / CellSize);
            int maxY = (int)MathF.Floor((y + radius) / CellSize);

            float sqrRadius = radius * radius;
            var result = new HashSet<IWorldObject2D>();

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    string key = GetKey(cx, cy);
                    if (!Grid.TryGetValue(key, out var list))
                        continue;

                    var instanceList = list
                        .Select(id => InstanceMap[id])
                        .Where(e => e.ZoneId == zoneId)
                        .ToList();

                    foreach (var obj in instanceList)
                    {
                        if (obj.ObjectArchetype != archetype)
                            continue;

                        float dx = obj.Transform.Position.X - x;
                        float dy = obj.Transform.Position.Y - y;

                        if ((dx * dx + dy * dy) <= sqrRadius)
                            result.Add(obj);
                    }
                }
            }

            return result;
        }

        /// <summary>Objects with this archetype, keyed by instance id (a new dictionary; empty when none).</summary>
        public virtual Dictionary<string, IWorldObject2D> GetByType(string archetype)
        {
            return (TypeMap.TryGetValue(archetype, out var map) ? map : new())
                .ToDictionary(id => id, id => InstanceMap[id]);
        }

        /// <summary>Objects with this archetype (a new set).</summary>
        public virtual HashSet<IWorldObject2D> GetAllByType(string archetype)
        {
            return GetByType(archetype).Values.ToHashSet();
        }
    }
}
