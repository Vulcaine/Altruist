using System.Numerics;

using Altruist.Numerics;

namespace Altruist.Gaming
{

    /// <summary>
    /// Configuration of one 3D world: placement, size and gravity on top of the common <see cref="IWorldIndex"/> fields.
    /// One instance per entry under <c>altruist:game:worlds:items</c>; each becomes an <see cref="Altruist.Gaming.ThreeD.IGameWorldManager3D"/>
    /// via <see cref="Altruist.Gaming.ThreeD.IWorldLoader3D"/>. For 2D worlds see <c>IWorldIndex2D</c>.
    /// </summary>
    public interface IWorldIndex3D : IWorldIndex
    {
        /// <summary>World origin offset (config <c>*:position</c>, default 0,0,0). Partitions are currently laid out from (0,0,0) regardless.</summary>
        Vector3 Position { get; set; }
        /// <summary>World extent in integer world units (config <c>*:size</c>); drives the partition grid.</summary>
        IntVector3 Size { get; set; }
        /// <summary>Gravity acceleration for the physics world in units/s² (config <c>*:gravity</c>, default (0, -9.81, 0); +Y is up).</summary>
        Vector3 Gravity { get; set; }

    }

    /// <summary>
    /// Default <see cref="IWorldIndex3D"/> bound from configuration: one instance per item of <c>altruist:game:worlds:items</c>
    /// (keyed by <c>id</c>) when <c>altruist:environment:mode</c> is <c>3D</c>. Size, gravity, position and fixed delta are live
    /// config values (re-bound on config reload).
    /// </summary>
    /// <example>
    /// <code>
    /// altruist:
    ///   environment: { mode: 3D }
    ///   game:
    ///     worlds:
    ///       items:
    ///         - id: overworld
    ///           index: 0
    ///           size: { x: 1024, y: 256, z: 1024 }
    ///           gravity: { x: 0, y: -9.81, z: 0 }
    ///           data-path: worlds/overworld.json   # optional, see WorldLoader3D
    /// </code>
    /// </example>
    [Service(typeof(IWorldIndex3D))]
    [ConditionalOnConfig("altruist:game:worlds")]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game:worlds:items", KeyField = "id")]
    public sealed class WorldIndex3D : VaultModel, IWorldIndex3D
    {
        /// <summary>Storage key (random GUID per instance).</summary>
        public override string StorageId { get; set; }

        /// <inheritdoc/>
        public string? DataPath { get; set; }
        /// <inheritdoc/>
        public Vector3 Position { get; set; }
        /// <inheritdoc/>
        public IntVector3 Size { get; set; }
        /// <inheritdoc/>
        public Vector3 Gravity { get; set; }
        /// <inheritdoc/>
        public float FixedDeltaTime { get; set; }

        /// <inheritdoc/>
        public int Index { get; set; }
        /// <inheritdoc/>
        public string Name { get; set; }

        /// <summary>Creation time (UTC).</summary>
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>Model type discriminator (<c>"WorldIndex3D"</c>).</summary>
        public override string Type { get; set; } = "WorldIndex3D";

        /// <summary>Binds the world index from its config section (<c>*</c> = the current <c>altruist:game:worlds:items</c> entry).</summary>
        /// <param name="index">Numeric world index (<c>*:index</c>); key used by the organizer.</param>
        /// <param name="name">Display name (<c>*:name</c>); falls back to <paramref name="id"/>, then <c>"World {index}"</c>.</param>
        /// <param name="id">Config item id (<c>*:id</c>).</param>
        /// <param name="liveDelta">Physics fixed step in seconds (<c>*:fixedDeltaTime</c>, default 0.01666).</param>
        /// <param name="liveSize">World size (<c>*:size</c>).</param>
        /// <param name="liveGravity">Gravity (<c>*:gravity</c>).</param>
        /// <param name="livePosition">Origin offset (<c>*:position</c>).</param>
        /// <param name="data">Optional path to a world JSON file (<c>*:data-path</c>).</param>
        public WorldIndex3D(
        [AppConfigValue("*:index")]
        int index,

        [AppConfigValue("*:name", null)]
        string? name,

        [AppConfigValue("*:id", null)]
        string? id,

        [AppConfigValue("*:fixedDeltaTime", "0.01666f")]
        ILiveConfigValue<float> liveDelta,

        [AppConfigValue("*:size")]
        ILiveConfigValue<IntVector3> liveSize,

        [AppConfigValue("*:gravity")]
        ILiveConfigValue<Vector3?> liveGravity,

        [AppConfigValue("*:position")]
        ILiveConfigValue<Vector3?> livePosition,
        [AppConfigValue("*:data-path")]
        string? data = null
        )
        {
            StorageId = Guid.NewGuid().ToString();
            Index = index;
            Name = ResolveWorldName(name, id, index);
            DataPath = data;

            liveSize.BindTo(v => Size = v);
            liveDelta.BindTo(v => FixedDeltaTime = v);

            liveGravity.BindTo(v => Gravity = v ?? new Vector3(0f, -9.81f, 0f));
            livePosition.BindTo(v => Position = v ?? Vector3.Zero);
        }

        /// <summary>Shortcut for <c>Size.X</c>.</summary>
        public int Width => Size.X;
        /// <summary>Shortcut for <c>Size.Y</c>.</summary>
        public int Height => Size.Y;
        /// <summary>Shortcut for <c>Size.Z</c>.</summary>
        public int Depth => Size.Z;

        private static string ResolveWorldName(string? name, string? id, int index)
        {
            if (!string.IsNullOrWhiteSpace(name))
                return name.Trim();

            if (!string.IsNullOrWhiteSpace(id))
                return id.Trim();

            return $"World {index}";
        }
    }
}
