
namespace Altruist.Gaming.ThreeD
{
    public class SpatialGridIndex3D
    {
        public int CellSize { get; set; }

        // All objects by instance id
        public Dictionary<string, ObjectMetadata3D> InstanceMap { get; set; } = new();

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

        public virtual void Add(WorldObjectTypeKey type, ObjectMetadata3D obj)
        {
            string key = GetKey(
                (int)(obj.Position.X / CellSize),
                (int)(obj.Position.Y / CellSize),
                (int)(obj.Position.Z / CellSize));

            if (!Grid.TryGetValue(key, out var list))
                Grid[key] = list = new HashSet<string>();

            list.Add(obj.InstanceId);
            InstanceMap[obj.InstanceId] = obj;

            var typeKey = type.Value;
            if (!TypeMap.TryGetValue(typeKey, out var typeSet))
                TypeMap[typeKey] = typeSet = new HashSet<string>();

            typeSet.Add(obj.InstanceId);
        }

        public virtual ObjectMetadata3D? Remove(WorldObjectTypeKey type, string instanceId)
        {
            if (!InstanceMap.TryGetValue(instanceId, out var obj))
                return null;

            string key = GetKey(
                (int)(obj.Position.X / CellSize),
                (int)(obj.Position.Y / CellSize),
                (int)(obj.Position.Z / CellSize));

            if (Grid.TryGetValue(key, out var cellSet))
                cellSet.Remove(instanceId);

            if (TypeMap.TryGetValue(type.Value, out var typeSet))
                typeSet.Remove(instanceId);

            InstanceMap.Remove(instanceId);
            return obj;
        }

        public virtual IEnumerable<ObjectMetadata3D> Query(
            WorldObjectTypeKey type,
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
            var result = new HashSet<ObjectMetadata3D>();

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

                        foreach (var obj in instanceList)
                        {
                            if (obj.Type != type) continue;

                            float dx = obj.Position.X - x;
                            float dy = obj.Position.Y - y;
                            float dz = obj.Position.Z - z;

                            if ((dx * dx + dy * dy + dz * dz) <= sqrRadius)
                                result.Add(obj);
                        }
                    }
                }
            }

            return result;
        }

        public virtual Dictionary<string, ObjectMetadata3D> GetByType(WorldObjectTypeKey type)
        {
            return (TypeMap.TryGetValue(type.Value, out var set) ? set : new())
                .ToDictionary(id => id, id => InstanceMap[id]);
        }

        public virtual HashSet<ObjectMetadata3D> GetAllByType(WorldObjectTypeKey type)
        {
            return GetByType(type).Values.ToHashSet();
        }
    }
}
