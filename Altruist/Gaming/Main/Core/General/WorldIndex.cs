namespace Altruist.Gaming
{
    /// <summary>
    /// Identity and settings of one persistent world, bound from <c>altruist:game:worlds:items</c> (one entry per
    /// world, keyed by <c>id</c>). Dimension-independent base of <see cref="IWorldIndex2D"/> and
    /// <see cref="IWorldIndex3D"/>, which add position, size and gravity.
    /// </summary>
    public interface IWorldIndex
    {
        /// <summary>Seconds per physics step of the world (item key <c>fixedDeltaTime</c>; 3D default 0.01666).</summary>
        float FixedDeltaTime { get; set; }
        /// <summary>The world's index (unique; how organizers and <see cref="WorldSnapshot.WorldIndex"/> address it).</summary>
        public int Index { get; set; }
        /// <summary>The world's name (item key <c>name</c>, else derived from its id or index).</summary>
        public string Name { get; set; }
        /// <summary>Path of the world's static data to load (item key <c>data-path</c>), or null for an empty world.</summary>
        public string? DataPath { get; set; }
    }
}
