/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Uniform-grid spatial index used inside each <see cref="WorldPartitionManager3D"/>: objects are bucketed by
    /// <c>(int)(position / CellSize)</c> per axis and also indexed by instance id and archetype.
    /// </summary>
    /// <remarks>
    /// An object is bucketed by its position at <see cref="Add"/> time; the bucket is not updated when it moves, so re-add
    /// after moving (the world manager does this via <c>UpdateObjectPosition</c>). Truncation toward zero makes cell 0 span
    /// <c>(-CellSize, CellSize)</c>. Not thread-safe. For tick-scoped broadphase over a snapshot use <see cref="SpatialHashGrid"/>.
    /// </remarks>
    public class SpatialGridIndex3D
    {
        /// <summary>Cell edge length in world units; must be non-zero before <see cref="Add"/> is called.</summary>
        public int CellSize { get; set; }

        // All objects by instance id
        /// <summary>All indexed objects by instance id.</summary>
        public Dictionary<string, IWorldObject3D> InstanceMap { get; set; } = new();

        // Grid cell key => set of instance ids
        /// <summary>Cell key (<c>"x:y:z"</c> cell coordinates) to the instance ids bucketed in that cell.</summary>
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional: type/archetype filter map; archetype key => set of instance ids
        /// <summary>Archetype (<c>""</c> for none) to instance ids.</summary>
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        /// <summary>Creates an index with <see cref="CellSize"/> 0 (set it before use; for serializers).</summary>
        public SpatialGridIndex3D() { }

        /// <summary>Creates an index with the given cell size.</summary>
        /// <param name="cellSize">Cell edge length in world units.</param>
        public SpatialGridIndex3D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y, int z) => $"{x}:{y}:{z}";

        /// <summary>Indexes <paramref name="obj"/> by its current position, instance id and archetype (overwrites an entry with the same id in the maps).</summary>
        /// <param name="obj">The object to index.</param>
        public virtual void Add(IWorldObject3D obj)
        {
            string key = GetKey(
                (int)(obj.Transform.Position.X / CellSize),
                (int)(obj.Transform.Position.Y / CellSize),
                (int)(obj.Transform.Position.Z / CellSize));

            if (!Grid.TryGetValue(key, out var list))
            {
                Grid[key] = list = new HashSet<string>();
            }

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var archetypeKey = obj.ObjectArchetype;
            if (!TypeMap.TryGetValue(archetypeKey ?? "", out var typeSet))
                TypeMap[archetypeKey ?? ""] = typeSet = new HashSet<string>();

            typeSet.Add(obj.InstanceId);
        }

        /// <summary>Removes an object; the grid cell is computed from its current position.</summary>
        /// <param name="instanceId">Instance id.</param>
        /// <returns>The removed object, or <c>null</c> if unknown.</returns>
        public virtual IWorldObject3D? Remove(string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj))
                return null;

            string key = GetKey(
                (int)(obj.Transform.Position.X / CellSize),
                (int)(obj.Transform.Position.Y / CellSize),
                (int)(obj.Transform.Position.Z / CellSize));

            if (Grid.TryGetValue(key, out var cellSet))
                cellSet.Remove(instanceId);

            if (TypeMap.TryGetValue(obj.ObjectArchetype ?? "", out var typeSet))
                typeSet.Remove(instanceId);

            InstanceMap.Remove(instanceId);
            return obj;
        }

        /// <summary>
        /// Returns objects of <paramref name="archetype"/> in zone <paramref name="roomId"/> whose position lies within a 3D sphere,
        /// scanning only the cells overlapping the sphere's bounding box.
        /// </summary>
        /// <param name="archetype">Archetype to match exactly.</param>
        /// <param name="x">Center X (world units).</param>
        /// <param name="y">Center Y (world units).</param>
        /// <param name="z">Center Z (world units).</param>
        /// <param name="radius">Radius in world units.</param>
        /// <param name="roomId">Zone id to match exactly (<c>ZoneId</c>).</param>
        /// <returns>A new set of matches.</returns>
        public virtual IEnumerable<IWorldObject3D> Query(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            int minX = (int)((x - radius) / CellSize);
            int maxX = (int)((x + radius) / CellSize);
            int minY = (int)((y - radius) / CellSize);
            int maxY = (int)((y + radius) / CellSize);
            int minZ = (int)((z - radius) / CellSize);
            int maxZ = (int)((z + radius) / CellSize);

            float sqrRadius = radius * radius;
            var result = new HashSet<IWorldObject3D>();

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    for (int cz = minZ; cz <= maxZ; cz++)
                    {
                        string key = GetKey(cx, cy, cz);
                        if (!Grid.TryGetValue(key, out var cellIds))
                            continue;

                        // Iterate directly — no LINQ, no intermediate list
                        foreach (var id in cellIds)
                        {
                            if (!InstanceMap.TryGetValue(id, out var obj))
                                continue;
                            if (obj.ZoneId != roomId)
                                continue;
                            if (obj.ObjectArchetype != archetype)
                                continue;

                            float dx = obj.Transform.Position.X - x;
                            float dy = obj.Transform.Position.Y - y;
                            float dz = obj.Transform.Position.Z - z;

                            if ((dx * dx + dy * dy + dz * dz) <= sqrRadius)
                                result.Add(obj);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>Lazily enumerates (instance id, object) pairs of one archetype.</summary>
        /// <param name="archetype">Archetype to match.</param>
        public virtual IEnumerable<KeyValuePair<string, IWorldObject3D>> GetByType(string archetype)
        {
            if (!TypeMap.TryGetValue(archetype, out var set))
                yield break;

            foreach (var id in set)
            {
                if (InstanceMap.TryGetValue(id, out var obj))
                    yield return new KeyValuePair<string, IWorldObject3D>(id, obj);
            }
        }

        /// <summary>Lazily enumerates objects of one archetype.</summary>
        /// <param name="archetype">Archetype to match.</param>
        public virtual IEnumerable<IWorldObject3D> GetAllByType(string archetype)
        {
            if (!TypeMap.TryGetValue(archetype, out var set))
                yield break;

            foreach (var id in set)
            {
                if (InstanceMap.TryGetValue(id, out var obj))
                    yield return obj;
            }
        }
    }
}
