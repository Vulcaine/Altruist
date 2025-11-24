/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.TwoD
{
    public class SpatialGridIndex2D
    {
        public int CellSize { get; set; }

        // Use stringified keys like "x:y" to allow JSON serialization
        // Flatten all objects by instance id
        public Dictionary<string, IWorldObject2D> InstanceMap { get; set; } = new();

        // grid key => set of instance ids
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional, used for type filtering, archetype string => set of instance ids
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        public SpatialGridIndex2D() { }

        public SpatialGridIndex2D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y) => $"{x}:{y}";

        public virtual void Add(IWorldObject2D obj)
        {
            string key = GetKey(
                obj.Transform.Position.X / CellSize,
                obj.Transform.Position.Y / CellSize);

            if (!Grid.TryGetValue(key, out var list))
                Grid[key] = list = new HashSet<string>();

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var typeKey = obj.Archetype;
            if (!TypeMap.TryGetValue(typeKey, out var typeSet))
                TypeMap[typeKey] = typeSet = new HashSet<string>();

            typeSet.Add(obj.InstanceId);
        }

        public virtual IWorldObject2D? Remove(string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj))
                return null;

            string key = GetKey(
                obj.Transform.Position.X / CellSize,
                obj.Transform.Position.Y / CellSize);

            if (Grid.TryGetValue(key, out var list))
            {
                list.Remove(instanceId);
            }

            if (TypeMap.TryGetValue(obj.Archetype, out var map))
            {
                map.Remove(instanceId);
            }

            InstanceMap.Remove(instanceId);
            return obj;
        }

        public virtual IEnumerable<IWorldObject2D> Query(
            string archetype,
            int x, int y,
            float radius,
            string roomId)
        {
            int minX = (int)((x - radius) / CellSize);
            int maxX = (int)((x + radius) / CellSize);
            int minY = (int)((y - radius) / CellSize);
            int maxY = (int)((y + radius) / CellSize);

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
                        .Where(e => e.RoomId == roomId)
                        .ToList();

                    foreach (var obj in instanceList)
                    {
                        if (obj.Archetype != archetype)
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

        public virtual Dictionary<string, IWorldObject2D> GetByType(string archetype)
        {
            return (TypeMap.TryGetValue(archetype, out var map) ? map : new())
                .ToDictionary(id => id, id => InstanceMap[id]);
        }

        public virtual HashSet<IWorldObject2D> GetAllByType(string archetype)
        {
            return GetByType(archetype).Values.ToHashSet();
        }
    }
}
