/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming
{
    /// <summary>
    /// A named spatial region within a world.
    /// Zones must fit entirely inside a single partition.
    /// Multiple zones can exist within the same partition.
    /// </summary>
    /// <remarks>These are static spatial regions. For regions whose spawns materialise and despawn with player
    /// presence, see <see cref="IManagedZone"/> / <see cref="IZoneManager"/>.</remarks>
    public interface IZone
    {
        /// <summary>Unique zone name within its world.</summary>
        string Name { get; }
        /// <summary>Inactive zones are skipped by the zone managers' spatial lookups (FindZoneAt / FindZonesInBounds); managed zones toggle it with player presence.</summary>
        bool IsActive { get; set; }
    }

    /// <summary>
    /// 2D zone with axis-aligned rectangular bounds.
    /// </summary>
    public interface IZone2D : IZone
    {
        /// <summary>Minimum corner in world units.</summary>
        IntVector2 Position { get; }
        /// <summary>Extent in world units.</summary>
        IntVector2 Size { get; }
    }

    /// <summary>
    /// 3D zone with axis-aligned box bounds.
    /// </summary>
    public interface IZone3D : IZone
    {
        /// <summary>Minimum corner in world units.</summary>
        IntVector3 Position { get; }
        /// <summary>Extent in world units.</summary>
        IntVector3 Size { get; }
    }

    /// <summary>Default <see cref="IZone2D"/>; register with <see cref="IZoneManager2D"/>.</summary>
    public class Zone2D : IZone2D
    {
        /// <inheritdoc/>
        public string Name { get; }
        /// <inheritdoc/>
        public bool IsActive { get; set; } = true;
        /// <inheritdoc/>
        public IntVector2 Position { get; }
        /// <inheritdoc/>
        public IntVector2 Size { get; }

        /// <summary>Creates an active zone.</summary>
        public Zone2D(string name, IntVector2 position, IntVector2 size)
        {
            Name = name;
            Position = position;
            Size = size;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"Zone2D '{Name}' at {Position} size {Size} active={IsActive}";
    }

    /// <summary>Default <see cref="IZone3D"/>; register with <see cref="IZoneManager3D"/>.</summary>
    public class Zone3D : IZone3D
    {
        /// <inheritdoc/>
        public string Name { get; }
        /// <inheritdoc/>
        public bool IsActive { get; set; } = true;
        /// <inheritdoc/>
        public IntVector3 Position { get; }
        /// <inheritdoc/>
        public IntVector3 Size { get; }

        /// <summary>Creates an active zone.</summary>
        public Zone3D(string name, IntVector3 position, IntVector3 size)
        {
            Name = name;
            Position = position;
            Size = size;
        }

        /// <inheritdoc/>
        public override string ToString()
            => $"Zone3D '{Name}' at {Position} size {Size} active={IsActive}";
    }

    /// <summary>
    /// Manages spatial zones within a world.
    /// Zones are validated against partition boundaries — a zone cannot
    /// be larger than a partition and must fit entirely inside one.
    /// </summary>
    public interface IZoneManager<TZone> where TZone : IZone
    {
        /// <summary>
        /// Register a zone. Throws if the zone exceeds partition size
        /// or does not fit entirely within a single partition.
        /// </summary>
        TZone RegisterZone(TZone zone);

        /// <summary>Get a zone by name, or null if not found.</summary>
        TZone? GetZone(string name);

        /// <summary>Remove a zone by name. Returns true if removed.</summary>
        bool RemoveZone(string name);

        /// <summary>Get all registered zones.</summary>
        IEnumerable<TZone> GetAllZones();
    }

    /// <summary>
    /// Extends the zone manager with 2D spatial lookups.
    /// </summary>
    public interface IZoneManager2D : IZoneManager<IZone2D>
    {
        /// <summary>Find the zone containing the given world-space position, or null.</summary>
        IZone2D? FindZoneAt(int x, int y);

        /// <summary>Find all zones overlapping a rectangular region.</summary>
        IEnumerable<IZone2D> FindZonesInBounds(int minX, int minY, int maxX, int maxY);
    }

    /// <summary>
    /// Extends the zone manager with 3D spatial lookups.
    /// </summary>
    public interface IZoneManager3D : IZoneManager<IZone3D>
    {
        /// <summary>Find the zone containing the given world-space position, or null.</summary>
        IZone3D? FindZoneAt(int x, int y, int z);

        /// <summary>Find all zones overlapping a box region.</summary>
        IEnumerable<IZone3D> FindZonesInBounds(
            int minX, int minY, int minZ,
            int maxX, int maxY, int maxZ);
    }

    /// <summary>Thrown by <see cref="IZoneManager{TZone}.RegisterZone"/> when a zone is larger than a partition
    /// or does not fit inside one.</summary>
    public class ZoneValidationException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public ZoneValidationException(string message) : base(message) { }
    }
}
