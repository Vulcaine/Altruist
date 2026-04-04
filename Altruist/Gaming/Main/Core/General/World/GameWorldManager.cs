namespace Altruist.Gaming
{
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
        public readonly int WorldIndex;
        public readonly IReadOnlyList<ITypelessWorldObject> AllObjects;
        public readonly IReadOnlyDictionary<string, ITypelessWorldObject> Lookup;

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
