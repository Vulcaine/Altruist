/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    public class SpatialGridIndex3D
    {
        public int CellSize { get; set; }

        // All objects by instance id
        public Dictionary<string, IWorldObject3D> InstanceMap { get; set; } = new();

        // Grid cell key => set of instance ids
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional: type/archetype filter map; archetype key => set of instance ids
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        public SpatialGridIndex3D() { }

        public SpatialGridIndex3D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y, int z) => $"{x}:{y}:{z}";

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
