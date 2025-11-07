namespace Altruist.Gaming.TwoD
{

    public class SpatialGridIndex2D
    {
        public int CellSize { get; set; }

        // Use stringified keys like "x:y" to allow JSON serialization
        // Optional, flatten all objects by ID if needed
        public Dictionary<string, IObjectMetadata> InstanceMap { get; set; } = new();

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

        public virtual void Add(WorldObjectTypeKey type, IObjectMetadata obj)
        {
            if (obj is not ObjectMetadata2D obj2d)
            {
                return;
            }
            string key = GetKey((int)(obj2d.Position.X / CellSize), (int)(obj2d.Position.Y / CellSize));

            if (!Grid.TryGetValue(key, out var list))
                Grid[key] = list = new HashSet<string>();

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var typeKey = type.Value;
            if (!TypeMap.TryGetValue(typeKey, out var typeDict))
                TypeMap[typeKey] = typeDict = new HashSet<string>();

            typeDict.Add(obj.InstanceId);
        }

        public virtual IObjectMetadata? Remove(WorldObjectTypeKey type, string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj) || obj is not ObjectMetadata2D obj2d)
                return null;

            string key = GetKey(obj2d.Position.X / CellSize, obj2d.Position.Y / CellSize);
            if (Grid.TryGetValue(key, out var list))
            {
                list.Remove(instanceId);
            }

            if (TypeMap.TryGetValue(type.Value, out var map))
            {
                map.Remove(instanceId);
            }

            InstanceMap.Remove(instanceId);

            return obj;
        }

        public virtual IEnumerable<IObjectMetadata> Query(WorldObjectTypeKey type, int x, int y, float radius, string roomId)
        {
            int minX = (int)((x - radius) / CellSize);
            int maxX = (int)((x + radius) / CellSize);
            int minY = (int)((y - radius) / CellSize);
            int maxY = (int)((y + radius) / CellSize);

            float sqrRadius = radius * radius;
            var result = new HashSet<IObjectMetadata>();

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cy = minY; cy <= maxY; cy++)
                {
                    string key = GetKey(cx, cy);
                    if (!Grid.TryGetValue(key, out var list)) continue;

                    var instanceList = list.Select(e => InstanceMap[e]).Where(e => e.RoomId == roomId).ToList();

                    foreach (var obj in instanceList)
                    {
                        if (obj.Type != type || obj is not ObjectMetadata2D obj2d) continue;

                        float dx = obj2d.Position.X - x;
                        float dy = obj2d.Position.Y - y;

                        if ((dx * dx + dy * dy) <= sqrRadius)
                            result.Add(obj);
                    }
                }
            }

            return result;
        }

        public virtual Dictionary<string, IObjectMetadata> GetByType(WorldObjectTypeKey type)
        {
            return (TypeMap.TryGetValue(type.Value, out var map) ? map : new()).ToDictionary(x => x, x => InstanceMap[x]);
        }

        public virtual HashSet<IObjectMetadata> GetAllByType(WorldObjectTypeKey type)
        {
            return GetByType(type).Values.ToHashSet();
        }
    }

}