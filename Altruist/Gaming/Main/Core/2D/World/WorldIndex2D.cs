using System.Numerics;

using Altruist.Numerics;

namespace Altruist.Gaming
{
    /// <summary>Static description of one 2D world (index, name, bounds, gravity, fixed step, data
    /// file). Consumed by <see cref="Altruist.Gaming.TwoD.IGameWorldOrganizer2D.AddWorld"/> and
    /// <see cref="Altruist.Gaming.TwoD.IWorldLoader2D"/> to build a running
    /// <see cref="Altruist.Gaming.TwoD.IGameWorldManager2D"/>. The 3D counterpart is <c>IWorldIndex3D</c>.</summary>
    public interface IWorldIndex2D : IWorldIndex
    {
        /// <summary>World-space origin of the world's area (lower-left corner used for partitioning).</summary>
        Vector2 Position { get; set; }
        /// <summary>World width (X) and height (Y) in world units.</summary>
        IntVector2 Size { get; set; }
        /// <summary>Gravity vector for the physics world, units/s² (default <c>(0, -9.81)</c>, +Y up).</summary>
        Vector2 Gravity { get; set; }
    }

    /// <summary>Config-bound <see cref="IWorldIndex2D"/>: one instance is registered per entry of
    /// <c>altruist:game:worlds:items</c> (keyed by <c>id</c>) when <c>altruist:environment:mode</c> is
    /// <c>2D</c>. Each entry's keys are read relative to the item (<c>index</c>, <c>name</c>,
    /// <c>id</c>, <c>fixedDeltaTime</c>, <c>size</c>, <c>gravity</c>, <c>position</c>, <c>data-path</c>).
    /// <example><code>
    /// altruist:
    ///   environment: { mode: 2D }
    ///   game:
    ///     worlds:
    ///       items:
    ///         - id: arena
    ///           index: 0
    ///           size: { x: 200, y: 100 }
    ///           gravity: { x: 0, y: -20 }
    ///           data-path: worlds/arena.json
    /// </code></example></summary>
    [Service(typeof(IWorldIndex2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [ConditionalOnConfig("altruist:game:worlds:items", KeyField = "id")]
    public sealed class WorldIndex2D : VaultModel, IWorldIndex2D
    {
        /// <summary>Storage key (a new GUID per instance).</summary>
        public override string StorageId { get; set; }

        /// <summary>Optional path of the JSON world file (<see cref="Altruist.Gaming.TwoD.WorldSchema2D"/>)
        /// loaded by <see cref="Altruist.Gaming.TwoD.IWorldLoader2D.LoadFromIndex"/>; config key <c>data-path</c>.
        /// The 2D organizer does not load it by itself: call the loader explicitly.</summary>
        public string? DataPath { get; set; }
        /// <inheritdoc/>
        public Vector2 Position { get; set; }
        /// <inheritdoc/>
        public IntVector2 Size { get; set; }
        /// <inheritdoc/>
        public Vector2 Gravity { get; set; }
        /// <summary>Physics fixed step in seconds (config <c>fixedDeltaTime</c>, default 0.01666).</summary>
        public float FixedDeltaTime { get; set; }
        /// <summary>Numeric world index, unique per organizer (config <c>index</c>).</summary>
        public int Index { get; set; }
        /// <summary>Display / lookup name: config <c>name</c>, else <c>id</c>, else <c>"World {index}"</c> (trimmed).</summary>
        public string Name { get; set; }
        /// <summary>Creation timestamp (UTC).</summary>
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
        /// <summary>Discriminator: <c>WorldIndex2D</c>.</summary>
        public override string Type { get; set; } = "WorldIndex2D";

        /// <summary>Binds one world entry from config (keys relative to the item, see the class summary).</summary>
        /// <param name="index">Config <c>index</c>: numeric world index.</param>
        /// <param name="name">Config <c>name</c> (optional).</param>
        /// <param name="id">Config <c>id</c> (optional; name fallback).</param>
        /// <param name="fixedDeltaTime">Config <c>fixedDeltaTime</c> in seconds (default 0.01666).</param>
        /// <param name="size">Config <c>size</c>: width / height in world units.</param>
        /// <param name="gravity">Config <c>gravity</c>; defaults to <c>(0, -9.81)</c>.</param>
        /// <param name="position">Config <c>position</c>; defaults to the origin.</param>
        /// <param name="data">Config <c>data-path</c>: optional JSON world file.</param>
        public WorldIndex2D(
            [AppConfigValue("*:index")]
            int index,
            [AppConfigValue("*:name", null)]
            string? name,
            [AppConfigValue("*:id", null)]
            string? id,
            [AppConfigValue("*:fixedDeltaTime", "0.01666f")]
            float fixedDeltaTime,
            [AppConfigValue("*:size")]
            IntVector2 size,
            [AppConfigValue("*:gravity")]
            Vector2? gravity = null,
            [AppConfigValue("*:position")]
            Vector2? position = null,
            [AppConfigValue("*:data-path")]
            string? data = null)
        {
            StorageId = Guid.NewGuid().ToString();
            Index = index;
            Size = size;
            FixedDeltaTime = fixedDeltaTime;
            Gravity = gravity ?? new Vector2(0f, -9.81f);
            Position = position ?? Vector2.Zero;
            DataPath = data;
            Name = ResolveWorldName(name, id, index);
        }

        /// <summary>World width (<c>Size.X</c>).</summary>
        public int Width => Size.X;
        /// <summary>World height (<c>Size.Y</c>).</summary>
        public int Height => Size.Y;

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
