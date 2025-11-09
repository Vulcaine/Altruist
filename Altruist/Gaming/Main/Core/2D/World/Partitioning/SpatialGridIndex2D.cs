namespace Altruist.Gaming.TwoD
{

    public class SpatialGridIndex2D
    {
        public int CellSize { get; set; }

        // Use stringified keys like "x:y" to allow JSON serialization
        // Optional, flatten all objects by ID if needed
        public Dictionary<string, IPrefab2D> InstanceMap { get; set; } = new();

        // grid key => metadata id string
        public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

        // Optional, used for type filtering, type string => metadata id string
        public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

        public SpatialGridIndex2D() { }

        public SpatialGridIndex2D(int cellSize)
        {
            CellSize = cellSize;
        }

        private static string GetKey(int x, int y) => $"{x}:{y}";

        public virtual void Add(IPrefab2D prefab)
        {
            string key = GetKey(prefab.Transform.Position.X / CellSize, prefab.Transform.Position.Y / CellSize);

            if (!Grid.TryGetValue(key, out var list))
                Grid[key] = list = new HashSet<string>();

            list.Add(prefab.InstanceId);
            InstanceMap[prefab.InstanceId] = prefab;

            var typeKey = prefab.PrefabId;
            if (!TypeMap.TryGetValue(typeKey, out var typeDict))
                TypeMap[typeKey] = typeDict = new HashSet<string>();

            typeDict.Add(prefab.InstanceId);
        }

        public virtual IPrefab2D? Remove(string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj))
                return null;

            string key = GetKey(obj.Transform.Position.X / CellSize, obj.Transform.Position.Y / CellSize);
            if (Grid.TryGetValue(key, out var list))
            {
                list.Remove(instanceId);
            }

            if (TypeMap.TryGetValue(obj.PrefabId, out var map))
            {
                map.Remove(instanceId);
            }

            InstanceMap.Remove(instanceId);

            return obj;
        }

        public virtual IEnumerable<IPrefab2D> Query(string prefabId, int x, int y, float radius, string roomId)
        {
            int minX = (int)((x - radius) / CellSize);
            int maxX = (int)((x + radius) / CellSize);
            int minY = (int)((y - radius) / CellSize);
            int maxY = (int)((y + radius) / CellSize);

            float sqrRadius = radius * radius;
            var result = new HashSet<IPrefab2D>();

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    string key = GetKey(cx, cy);
                    if (!Grid.TryGetValue(key, out var list)) continue;

                    var instanceList = list.Select(e => InstanceMap[e]).Where(e => e.RoomId == roomId).ToList();

                    foreach (var prefab2D in instanceList)
                    {
                        if (prefab2D.PrefabId != prefabId) continue;

                        float dx = prefab2D.Transform.Position.X - x;
                        float dy = prefab2D.Transform.Position.Y - y;

                        if ((dx * dx + dy * dy) <= sqrRadius)
                            result.Add(prefab2D);
                    }
                }
            }

            return result;
        }

        public virtual Dictionary<string, IPrefab2D> GetByType(string prefabId)
        {
            return (TypeMap.TryGetValue(prefabId, out var map) ? map : new()).ToDictionary(x => x, x => InstanceMap[x]);
        }

        public virtual HashSet<IPrefab2D> GetAllByType(string prefabId)
        {
            return GetByType(prefabId).Values.ToHashSet();
        }
    }

}