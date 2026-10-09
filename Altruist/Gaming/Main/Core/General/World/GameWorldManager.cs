namespace Altruist.Gaming
{
    /// <summary>Marker base of the per-world managers (<c>IGameWorldManager2D</c> / <c>IGameWorldManager3D</c>),
    /// which hold one world's objects and physics. Declares no members; use the dimension-specific interface.</summary>
    public interface IGameWorldManager
    {

    }

    /// <summary>
    /// Pre-computed, allocation-free snapshot of a world's entity list for a single tick.
    /// Shared across AI, visibility, and sync subsystems to avoid repeated materialization.
    /// Dimension-agnostic: works with both 2D and 3D world objects via ITypelessWorldObject.
    /// </summary>
    public readonly struct WorldSnapshot
    {
        /// <summary>Index of the world the snapshot was taken from.</summary>
        public readonly int WorldIndex;
        /// <summary>Every object in the world this tick. Do not mutate.</summary>
        public readonly IReadOnlyList<ITypelessWorldObject> AllObjects;
        /// <summary>The same objects by <see cref="ITypelessWorldObject.InstanceId"/>.</summary>
        public readonly IReadOnlyDictionary<string, ITypelessWorldObject> Lookup;

        /// <summary>Creates a snapshot (built by the world organizer each tick).</summary>
        public WorldSnapshot(
            int worldIndex,
            IReadOnlyList<ITypelessWorldObject> allObjects,
            IReadOnlyDictionary<string, ITypelessWorldObject> lookup)
        {
            WorldIndex = worldIndex;
            AllObjects = allObjects;
            Lookup = lookup;
        }
    }
}
