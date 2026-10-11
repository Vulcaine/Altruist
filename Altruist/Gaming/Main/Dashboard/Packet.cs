using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Reflection;

using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

using MessagePack;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Delta packet (message code <see cref="PacketCodes.DashboardWorldObjectState"/>) sent by <see cref="DashboardPortal"/>
    /// to dashboard viewers: changed objects grouped by partition, removed objects, and gizmo upserts/removals for one world.
    /// JSON and MessagePack compatible.
    /// </summary>
    /// <remarks>Only changed data is included; viewers apply it on top of the HTTP snapshot from <see cref="WorldDashboardController"/>.</remarks>
    [MessagePackObject]
    public sealed class DashboardWorldObjectStatePacket : IPacketBase
    {
        /// <summary>Packet code; always <see cref="PacketCodes.DashboardWorldObjectState"/>.</summary>
        [JsonPropertyName("messageCode")]
        [Key(0)]
        public uint MessageCode { get; set; }

        /// <summary>World the update belongs to.</summary>
        [JsonPropertyName("worldIndex")]
        [Key(1)]
        public int WorldIndex { get; set; }

        /// <summary>Server time the diff was taken (UTC).</summary>
        [JsonPropertyName("timestampUtc")]
        [Key(2)]
        public DateTime TimestampUtc { get; set; }

        /// <summary>Partitions containing objects that were added or changed.</summary>
        [JsonPropertyName("partitions")]
        [Key(3)]
        public DashboardPartitionStateDto[] Partitions { get; set; }

        /// <summary>Gizmos added or changed since the last packet (visibility-radius gizmos are resent every packet).</summary>
        [JsonPropertyName("gizmos")]
        [Key(4)]
        public DashboardGizmo[] Gizmos { get; set; }

        /// <summary>Ids of gizmos to delete.</summary>
        [JsonPropertyName("removedGizmoIds")]
        [Key(5)]
        public string[] RemovedGizmoIds { get; set; }

        /// <summary>Instance ids of objects that left the world (or the visibility filter).</summary>
        [JsonPropertyName("removedObjectIds")]
        [Key(6)]
        public string[] RemovedObjectIds { get; set; }

        /// <summary>Creates an empty packet (for deserializers).</summary>
        public DashboardWorldObjectStatePacket()
        {
            MessageCode = PacketCodes.DashboardWorldObjectState;
            Partitions = Array.Empty<DashboardPartitionStateDto>();
            Gizmos = Array.Empty<DashboardGizmo>();
            RemovedGizmoIds = Array.Empty<string>();
            RemovedObjectIds = Array.Empty<string>();
        }

        /// <summary>Creates a populated packet; null arrays become empty.</summary>
        /// <param name="worldIndex">World index.</param>
        /// <param name="timestampUtc">Diff time (UTC).</param>
        /// <param name="partitions">Changed partitions.</param>
        /// <param name="gizmos">Changed gizmos.</param>
        /// <param name="removedGizmoIds">Removed gizmo ids.</param>
        /// <param name="removedObjectIds">Removed object instance ids.</param>
        public DashboardWorldObjectStatePacket(
            int worldIndex,
            DateTime timestampUtc,
            DashboardPartitionStateDto[] partitions,
            DashboardGizmo[]? gizmos = null,
            string[]? removedGizmoIds = null,
            string[]? removedObjectIds = null)
        {
            MessageCode = PacketCodes.DashboardWorldObjectState;
            WorldIndex = worldIndex;
            TimestampUtc = timestampUtc;
            Partitions = partitions ?? Array.Empty<DashboardPartitionStateDto>();
            Gizmos = gizmos ?? Array.Empty<DashboardGizmo>();
            RemovedGizmoIds = removedGizmoIds ?? Array.Empty<string>();
            RemovedObjectIds = removedObjectIds ?? Array.Empty<string>();
        }
    }

    /// <summary>Changed objects of one world partition inside a <see cref="DashboardWorldObjectStatePacket"/>.</summary>
    [MessagePackObject]
    public sealed class DashboardPartitionStateDto
    {
        /// <summary>Partition grid index X.</summary>
        [JsonPropertyName("x")]
        [Key(0)]
        public int X { get; set; }

        /// <summary>Partition grid index Y.</summary>
        [JsonPropertyName("y")]
        [Key(1)]
        public int Y { get; set; }

        /// <summary>Partition grid index Z.</summary>
        [JsonPropertyName("z")]
        [Key(2)]
        public int Z { get; set; }

        /// <summary>Objects in this partition that changed since the last packet.</summary>
        [JsonPropertyName("objects")]
        [Key(3)]
        public IReadOnlyList<DashboardWorldObjectStateDto> Objects { get; set; } = Array.Empty<DashboardWorldObjectStateDto>();
    }

    /// <summary>Minimal per-object state streamed to the dashboard (identity and position only).</summary>
    [MessagePackObject]
    public sealed class DashboardWorldObjectStateDto
    {
        /// <summary>World object instance id.</summary>
        [JsonPropertyName("id")]
        [Key(0)]
        public string InstanceId { get; set; } = string.Empty;

        /// <summary>Object archetype (empty if none).</summary>
        [JsonPropertyName("archetype")]
        [Key(1)]
        public string Archetype { get; set; } = string.Empty;

        /// <summary>World position (from the physics body when the object has one).</summary>
        [JsonPropertyName("position")]
        [Key(2)]
        public Vector3Dto Position { get; set; } = default!;

        /// <summary>Value of a public string <c>Name</c> property on the object, if it has one; otherwise empty.</summary>
        [JsonPropertyName("name")]
        [Key(3)]
        public string Name { get; set; } = string.Empty;
    }

    internal static class DashboardWorldObjectNames
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo?> NameProperties = new();

        public static string Resolve(object? obj)
        {
            if (obj is null)
                return string.Empty;

            var property = NameProperties.GetOrAdd(obj.GetType(), static type =>
            {
                var candidate = type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
                return candidate?.PropertyType == typeof(string) ? candidate : null;
            });

            return property?.GetValue(obj) as string ?? string.Empty;
        }
    }

    /// <summary>Serializable 3D vector (world units, +Y up) used by dashboard DTOs and gizmos.</summary>
    [MessagePackObject]
    public sealed class Vector3Dto
    {
        /// <summary>X component.</summary>
        [JsonPropertyName("x")]
        [Key(0)]
        public float X { get; set; }

        /// <summary>Y component (up).</summary>
        [JsonPropertyName("y")]
        [Key(1)]
        public float Y { get; set; }

        /// <summary>Z component.</summary>
        [JsonPropertyName("z")]
        [Key(2)]
        public float Z { get; set; }
    }

    /// <summary>One vertex of a <see cref="DashboardGizmo"/> polyline/polygon (<see cref="DashboardGizmo.Points"/>), world space.</summary>
    [MessagePackObject]
    public sealed class DashboardGizmoPoint
    {
        /// <summary>X component.</summary>
        [JsonPropertyName("x")]
        [Key(0)]
        public float X { get; set; }

        /// <summary>Y component (up).</summary>
        [JsonPropertyName("y")]
        [Key(1)]
        public float Y { get; set; }

        /// <summary>Z component.</summary>
        [JsonPropertyName("z")]
        [Key(2)]
        public float Z { get; set; }
    }

    /// <summary>
    /// A debug shape drawn over a world in the dashboard viewer. Publish it through <see cref="IDashboardGizmoRegistry"/>.
    /// </summary>
    /// <remarks>
    /// Shapes by <see cref="Type"/> (as rendered by the bundled viewer): <c>sphere</c> (default; <see cref="Radius"/>),
    /// <c>circle</c> (on the XZ plane at <see cref="Position"/>; <see cref="Radius"/>), <c>rect</c> (XZ outline from corner
    /// <see cref="Position"/> spanning <see cref="Width"/> x <see cref="Height"/>), <c>solid-rect</c>/<c>filled-rect</c> (filled XZ
    /// rect centred on <see cref="Position"/>), <c>box</c> (wireframe <see cref="Width"/> x <see cref="Height"/> x <see cref="Radius"/>
    /// depth), <c>polyline</c> (&gt;= 2 <see cref="Points"/>) and <c>polygon</c>/<c>cone</c> (closed, &gt;= 3 <see cref="Points"/>).
    /// Unknown types render as a sphere. All coordinates are world units.
    /// </remarks>
    [MessagePackObject]
    public sealed class DashboardGizmo
    {
        /// <summary>Unique, stable id (ordinal); upserting the same id replaces the gizmo. Required.</summary>
        [JsonPropertyName("id")]
        [Key(0)]
        public string Id { get; set; } = string.Empty;

        /// <summary>World the gizmo is drawn in.</summary>
        [JsonPropertyName("worldIndex")]
        [Key(1)]
        public int WorldIndex { get; set; }

        /// <summary>Free-form layer name used for grouping/toggling in the viewer and by <see cref="IDashboardGizmoRegistry.ClearCategory"/>.</summary>
        [JsonPropertyName("category")]
        [Key(2)]
        public string Category { get; set; } = string.Empty;

        /// <summary>Free-form producer name (e.g. your module); <c>altruist</c> is used for framework gizmos.</summary>
        [JsonPropertyName("source")]
        [Key(3)]
        public string Source { get; set; } = string.Empty;

        /// <summary>Shape kind; see the class remarks. Default <c>sphere</c>.</summary>
        [JsonPropertyName("type")]
        [Key(4)]
        public string Type { get; set; } = "sphere";

        /// <summary>Optional text label.</summary>
        [JsonPropertyName("label")]
        [Key(5)]
        public string Label { get; set; } = string.Empty;

        /// <summary>Colour as <c>#RRGGBBAA</c> hex (alpha drives opacity). Default <c>#38BDF8FF</c>.</summary>
        [JsonPropertyName("color")]
        [Key(6)]
        public string Color { get; set; } = "#38BDF8FF";

        /// <summary>Anchor/centre in world space (corner for <c>rect</c>).</summary>
        [JsonPropertyName("position")]
        [Key(7)]
        public Vector3Dto Position { get; set; } = new();

        /// <summary>Radius for <c>sphere</c>/<c>circle</c>; depth for <c>box</c>.</summary>
        [JsonPropertyName("radius")]
        [Key(8)]
        public float Radius { get; set; }

        /// <summary>Width (X extent) for rect and box shapes.</summary>
        [JsonPropertyName("width")]
        [Key(9)]
        public float Width { get; set; }

        /// <summary>Height for rect shapes (Z extent) and box (Y extent).</summary>
        [JsonPropertyName("height")]
        [Key(10)]
        public float Height { get; set; }

        /// <summary>Vertices for <c>polyline</c>, <c>polygon</c> and <c>cone</c>.</summary>
        [JsonPropertyName("points")]
        [Key(11)]
        public List<DashboardGizmoPoint> Points { get; set; } = new();

        /// <summary>Optional instance id of the world object this gizmo relates to (metadata for the viewer).</summary>
        [JsonPropertyName("attachToInstanceId")]
        [Key(12)]
        public string AttachToInstanceId { get; set; } = string.Empty;

        /// <summary>Lifetime in seconds counted from <see cref="CreatedAtUtc"/>; 0 or less never expires.</summary>
        [JsonPropertyName("ttlSeconds")]
        [Key(13)]
        public float TtlSeconds { get; set; }

        /// <summary>Creation time (UTC); set by the registry when left default.</summary>
        [JsonPropertyName("createdAtUtc")]
        [Key(14)]
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>Last update time (UTC); set by the registry.</summary>
        [JsonPropertyName("updatedAtUtc")]
        [Key(15)]
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>Result of <see cref="IDashboardGizmoRegistry.DrainChanges"/>: gizmos changed and ids removed since the previous drain.</summary>
    public sealed class DashboardGizmoChangeSet
    {
        /// <summary>Copies of added or changed gizmos.</summary>
        public DashboardGizmo[] Gizmos { get; set; } = Array.Empty<DashboardGizmo>();
        /// <summary>Ids of removed or expired gizmos.</summary>
        public string[] RemovedGizmoIds { get; set; } = Array.Empty<string>();
    }

    /// <summary>Lightweight summary of a world, returned by <see cref="WorldDashboardController.GetWorlds"/>.</summary>
    public sealed class WorldSummaryDto
    {
        /// <summary>World index.</summary>
        public int Index { get; set; }
        /// <summary>World name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Number of partitions in the world.</summary>
        public int PartitionCount { get; set; }
        /// <summary>Number of objects that pass the dashboard visibility filter.</summary>
        public int ObjectCount { get; set; }
    }

    /// <summary>
    /// Flattened representation of a world object for dashboard use.
    /// Includes transform and collider descriptors.
    /// </summary>
    public sealed class WorldObjectDto
    {
        /// <summary>World object instance id.</summary>
        public string InstanceId { get; set; } = string.Empty;
        /// <summary>Object archetype (empty if none).</summary>
        public string Archetype { get; set; } = string.Empty;
        /// <summary>Value of a public string <c>Name</c> property on the object, if any.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Zone id of the object.</summary>
        public string ZoneId { get; set; } = string.Empty;
        /// <summary>Owning client id; empty for server-owned objects.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>Whether the object is flagged expired (pending removal).</summary>
        public bool Expired { get; set; }

        /// <summary>World transform (physics body pose when the object has a body).</summary>
        public TransformDto Transform { get; set; } = default!;

        /// <summary>Runtime colliders (world space) or, when the object has none, its collider descriptors (local space).</summary>
        public List<ColliderDto> Colliders { get; set; } = new();
    }

    /// <summary>
    /// Collider descriptor for dashboard UI, including heightfield if present.
    /// </summary>
    public sealed class ColliderDto
    {
        /// <summary>Collider id.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Collider shape.</summary>
        public PhysxColliderShape3D Shape { get; set; }
        /// <summary>Collider transform; its <c>Size</c> holds the shape dimensions (e.g. capsule = radius, halfLength).</summary>
        public TransformDto Transform { get; set; } = default!;
        /// <summary>Whether the collider is an overlap-only trigger.</summary>
        public bool IsTrigger { get; set; }
        /// <summary><c>"world"</c> for runtime colliders, <c>"local"</c> (relative to the object) for descriptors.</summary>
        public string TransformSpace { get; set; } = "local";

        /// <summary>Down-sampled heightfield for terrain colliders; null otherwise.</summary>
        public HeightfieldDto? Heightfield { get; set; }

        /// <summary>Builds a local-space DTO from a collider descriptor.</summary>
        /// <param name="c">Collider descriptor.</param>
        /// <param name="terrainSampleStride">Heightfield down-sampling stride (see <see cref="DashboardWorldViewOptions.TerrainSampleStride"/>).</param>
        /// <returns>The DTO.</returns>
        public static ColliderDto FromCollider(PhysxCollider3DDesc c, int terrainSampleStride = 1)
        {
            return new ColliderDto
            {
                Id = c.Id,
                Shape = c.Shape,
                IsTrigger = c.IsTrigger,
                TransformSpace = "local",
                Transform = TransformDto.FromTransform(c.Transform),
                Heightfield = c.Heightfield is null ? null : HeightfieldDto.FromHeightfield(c.Heightfield, terrainSampleStride)
            };
        }
    }

    /// <summary>
    /// Heightfield data for visualization (terrain).
    /// </summary>
    public sealed class HeightfieldDto
    {
        /// <summary>Number of samples along X after down-sampling.</summary>
        public int Width { get; set; }
        /// <summary>Number of samples along Z after down-sampling.</summary>
        public int Height { get; set; }

        /// <summary>Distance between samples along X, world units (source cell size x stride).</summary>
        public float CellSizeX { get; set; }
        /// <summary>Distance between samples along Z, world units (source cell size x stride).</summary>
        public float CellSizeZ { get; set; }
        /// <summary>Multiplier applied to raw height samples, as in the source heightfield.</summary>
        public float HeightScale { get; set; }

        /// <summary>
        /// Heights[x][z], same indexing as the engine's HeightfieldData.Heights[x,z].
        /// </summary>
        public float[][] Heights { get; set; } = Array.Empty<float[]>();

        /// <summary>Copies a heightfield, keeping every <paramref name="sampleStride"/>-th sample on each axis (clamped to the last row/column).</summary>
        /// <param name="hf">Source heightfield.</param>
        /// <param name="sampleStride">Stride (values below 1 are treated as 1).</param>
        /// <returns>The DTO.</returns>
        public static HeightfieldDto FromHeightfield(HeightfieldData hf, int sampleStride = 1)
        {
            int stride = Math.Max(1, sampleStride);
            int width = (int)Math.Ceiling(hf.Width / (double)stride);
            int height = (int)Math.Ceiling(hf.Height / (double)stride);

            var dto = new HeightfieldDto
            {
                Width = width,
                Height = height,
                CellSizeX = hf.CellSizeX * stride,
                CellSizeZ = hf.CellSizeZ * stride,
                HeightScale = hf.HeightScale,
                Heights = new float[width][]
            };

            for (int x = 0; x < width; x++)
            {
                var row = new float[height];
                int sourceX = Math.Min(hf.Width - 1, x * stride);

                for (int z = 0; z < height; z++)
                {
                    int sourceZ = Math.Min(hf.Height - 1, z * stride);
                    row[z] = hf.Heights[sourceX, sourceZ];
                }

                dto.Heights[x] = row;
            }

            return dto;
        }
    }

    /// <summary>Serializable transform for dashboard DTOs: position, size and scale (rotation is not included).</summary>
    public sealed class TransformDto
    {
        /// <summary>Position, world units.</summary>
        public Vector3Dto Position { get; set; } = default!;
        /// <summary>Shape size (meaning depends on the collider shape).</summary>
        public Vector3Dto Size { get; set; } = default!;
        /// <summary>Scale.</summary>
        public Vector3Dto Scale { get; set; } = default!;

        /// <summary>Converts a <see cref="Transform3D"/> (rotation dropped).</summary>
        /// <param name="t">Source transform.</param>
        /// <returns>The DTO.</returns>
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
