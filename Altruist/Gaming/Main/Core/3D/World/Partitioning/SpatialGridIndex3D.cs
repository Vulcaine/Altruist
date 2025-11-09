
namespace Altruist.Gaming.ThreeD
{
    public class SpatialGridIndex3D
    {
        public int CellSize { get; set; }

        // All objects by instance id
        public Dictionary<string, IPrefab3D> InstanceMap { get; set; } = new();

        // Grid cell key => set of instance ids
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional: type filter map; type key => set of instance ids
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        public SpatialGridIndex3D() { }

        public SpatialGridIndex3D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y, int z) => $"{x}:{y}:{z}";

        public virtual void Add(IPrefab3D obj)
        {
            string key = GetKey(
                (int)obj.Transform.Position.X / CellSize,
                (int)obj.Transform.Position.Y / CellSize,
                (int)obj.Transform.Position.Z / CellSize);

            if (!Grid.TryGetValue(key, out var list))
            {
                Grid[key] = list = new HashSet<string>();
            }

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var typeKey = obj.PrefabId;
            if (!TypeMap.TryGetValue(typeKey, out var typeSet))
                TypeMap[typeKey] = typeSet = new HashSet<string>();

            typeSet.Add(obj.InstanceId);
        }

        public virtual IPrefab3D? Remove(string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var prefab3D))
                return null;

            string key = GetKey(
                (int)prefab3D.Transform.Position.X / CellSize,
                (int)prefab3D.Transform.Position.Y / CellSize,
                (int)prefab3D.Transform.Position.Z / CellSize);

            if (Grid.TryGetValue(key, out var cellSet))
                cellSet.Remove(instanceId);

            if (TypeMap.TryGetValue(prefab3D.PrefabId, out var typeSet))
                typeSet.Remove(instanceId);

            InstanceMap.Remove(instanceId);
            return prefab3D;
        }

        public virtual IEnumerable<IPrefab3D> Query(
            string prefabId,
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
            var result = new HashSet<IPrefab3D>();

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    for (int cz = minZ; cz <= maxZ; cz++)
                    {
                        string key = GetKey(cx, cy, cz);
                        if (!Grid.TryGetValue(key, out var list)) continue;

                        var instanceList = list
                            .Select(id => InstanceMap[id])
                            .Where(e => e.RoomId == roomId)
                            .ToList();

                        foreach (var prefab3D in instanceList)
                        {
                            if (prefab3D.PrefabId != prefabId) continue;

                            float dx = prefab3D.Transform.Position.X - x;
                            float dy = prefab3D.Transform.Position.Y - y;
                            float dz = prefab3D.Transform.Position.Z - z;

                            if ((dx * dx + dy * dy + dz * dz) <= sqrRadius)
                                result.Add(prefab3D);
                        }
                    }
                }
            }

            return result;
        }

        public virtual Dictionary<string, IPrefab3D> GetByType(string prefabId)
        {
            return (TypeMap.TryGetValue(prefabId, out var set) ? set : new())
                .ToDictionary(id => id, id => InstanceMap[id]);
        }

        public virtual HashSet<IPrefab3D> GetAllByType(string prefabId)
        {
            return GetByType(prefabId).Values.ToHashSet();
        }
    }
}
