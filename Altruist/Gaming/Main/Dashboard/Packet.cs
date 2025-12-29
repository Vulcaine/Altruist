using System.Text.Json.Serialization;

using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

using MessagePack;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Packet carrying partitioned object state updates for dashboard.
    /// JSON + MessagePack compatible.
    /// </summary>
    [MessagePackObject]
    public sealed class DashboardWorldObjectStatePacket : IPacketBase
    {
        [JsonPropertyName("messageCode")]
        [Key(0)]
        public uint MessageCode { get; set; }

        [JsonPropertyName("worldIndex")]
        [Key(1)]
        public int WorldIndex { get; set; }

        [JsonPropertyName("timestampUtc")]
        [Key(2)]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("partitions")]
        [Key(3)]
        public DashboardPartitionStateDto[] Partitions { get; set; }

        public DashboardWorldObjectStatePacket()
        {
            MessageCode = PacketCodes.DashboardWorldObjectState;
            Partitions = Array.Empty<DashboardPartitionStateDto>();
        }

        public DashboardWorldObjectStatePacket(
            int worldIndex,
            DateTime timestampUtc,
            DashboardPartitionStateDto[] partitions)
        {
            MessageCode = PacketCodes.DashboardWorldObjectState;
            WorldIndex = worldIndex;
            TimestampUtc = timestampUtc;
            Partitions = partitions ?? Array.Empty<DashboardPartitionStateDto>();
        }
    }

    /// <summary>
    /// Single partition update payload.
    /// </summary>
    [MessagePackObject]
    public sealed class DashboardPartitionStateDto
    {
        [JsonPropertyName("x")]
        [Key(0)]
        public int X { get; set; }

        [JsonPropertyName("y")]
        [Key(1)]
        public int Y { get; set; }

        [JsonPropertyName("z")]
        [Key(2)]
        public int Z { get; set; }

        [JsonPropertyName("objects")]
        [Key(3)]
        public IReadOnlyList<DashboardWorldObjectStateDto> Objects { get; set; } = Array.Empty<DashboardWorldObjectStateDto>();
    }

    /// <summary>
    /// Minimal per-object state sent to the dashboard.
    /// </summary>
    [MessagePackObject]
    public sealed class DashboardWorldObjectStateDto
    {
        [JsonPropertyName("id")]
        [Key(0)]
        public string InstanceId { get; set; } = string.Empty;

        [JsonPropertyName("archetype")]
        [Key(1)]
        public string Archetype { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        [Key(2)]
        public Vector3Dto Position { get; set; } = default!;
    }

    /// <summary>
    /// Simple 3D vector DTO (position only for now).
    /// </summary>
    [MessagePackObject]
    public sealed class Vector3Dto
    {
        [JsonPropertyName("x")]
        [Key(0)]
        public float X { get; set; }

        [JsonPropertyName("y")]
        [Key(1)]
        public float Y { get; set; }

        [JsonPropertyName("z")]
        [Key(2)]
        public float Z { get; set; }
    }

    /// <summary>
    /// Lightweight summary of a world for dashboard display.
    /// </summary>
    public sealed class WorldSummaryDto
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;

        public int PartitionCount { get; set; }
        public int ObjectCount { get; set; }
    }

    /// <summary>
    /// Flattened representation of a world object for dashboard use.
    /// Includes transform and collider descriptors.
    /// </summary>
    public sealed class WorldObjectDto
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Archetype { get; set; } = string.Empty;
        public string ZoneId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;

        public bool Expired { get; set; }

        public TransformDto Transform { get; set; } = default!;

        public List<ColliderDto> Colliders { get; set; } = new();
    }

    /// <summary>
    /// Collider descriptor for dashboard UI, including heightfield if present.
    /// </summary>
    public sealed class ColliderDto
    {
        public string Id { get; set; } = string.Empty;
        public PhysxColliderShape3D Shape { get; set; }
        public TransformDto Transform { get; set; } = default!;
        public bool IsTrigger { get; set; }

        public HeightfieldDto? Heightfield { get; set; }

        public static ColliderDto FromCollider(PhysxCollider3DDesc c)
        {
            return new ColliderDto
            {
                Id = c.Id,
                Shape = c.Shape,
                IsTrigger = c.IsTrigger,
                Transform = TransformDto.FromTransform(c.Transform),
                Heightfield = c.Heightfield is null ? null : HeightfieldDto.FromHeightfield(c.Heightfield)
            };
        }
    }

    /// <summary>
    /// Heightfield data for visualization (terrain).
    /// </summary>
    public sealed class HeightfieldDto
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public float CellSizeX { get; set; }
        public float CellSizeZ { get; set; }
        public float HeightScale { get; set; }

        /// <summary>
        /// Heights[x][z], same indexing as the engine's HeightfieldData.Heights[x,z].
        /// </summary>
        public float[][] Heights { get; set; } = Array.Empty<float[]>();

        public static HeightfieldDto FromHeightfield(HeightfieldData hf)
        {
            var dto = new HeightfieldDto
            {
                Width = hf.Width,
                Height = hf.Height,
                CellSizeX = hf.CellSizeX,
                CellSizeZ = hf.CellSizeZ,
                HeightScale = hf.HeightScale,
                Heights = new float[hf.Width][]
            };

            for (int x = 0; x < hf.Width; x++)
            {
                var row = new float[hf.Height];
                for (int z = 0; z < hf.Height; z++)
                {
                    row[z] = hf.Heights[x, z];
                }
                dto.Heights[x] = row;
            }

            return dto;
        }
    }

    public sealed class TransformDto
    {
        public Vector3Dto Position { get; set; } = default!;
        public Vector3Dto Size { get; set; } = default!;
        public Vector3Dto Scale { get; set; } = default!;

        public static TransformDto FromTransform(Transform3D t)
        {
            return new TransformDto
            {
                Position = new Vector3Dto
                {
                    X = t.Position.X,
                    Y = t.Position.Y,
                    Z = t.Position.Z
                },
                Size = new Vector3Dto
                {
                    X = t.Size.X,
                    Y = t.Size.Y,
                    Z = t.Size.Z
                },
                Scale = new Vector3Dto
                {
                    X = t.Scale.X,
                    Y = t.Scale.Y,
                    Z = t.Scale.Z
                }
            };
        }
    }
}
