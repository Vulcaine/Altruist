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
    public interface IZone
    {
        string Name { get; }
        bool IsActive { get; set; }
    }

    /// <summary>
    /// 2D zone with axis-aligned rectangular bounds.
    /// </summary>
    public interface IZone2D : IZone
    {
        IntVector2 Position { get; }
        IntVector2 Size { get; }
    }

    /// <summary>
    /// 3D zone with axis-aligned box bounds.
    /// </summary>
    public interface IZone3D : IZone
    {
        IntVector3 Position { get; }
        IntVector3 Size { get; }
    }

    public class Zone2D : IZone2D
    {
        public string Name { get; }
        public bool IsActive { get; set; } = true;
        public IntVector2 Position { get; }
        public IntVector2 Size { get; }

        public Zone2D(string name, IntVector2 position, IntVector2 size)
        {
            Name = name;
            Position = position;
            Size = size;
        }

        public override string ToString()
            => $"Zone2D '{Name}' at {Position} size {Size} active={IsActive}";
    }

    public class Zone3D : IZone3D
    {
        public string Name { get; }
        public bool IsActive { get; set; } = true;
        public IntVector3 Position { get; }
        public IntVector3 Size { get; }

        public Zone3D(string name, IntVector3 position, IntVector3 size)
        {
            Name = name;
            Position = position;
            Size = size;
        }

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

    public class ZoneValidationException : Exception
    {
        public ZoneValidationException(string message) : base(message) { }
    }
}
