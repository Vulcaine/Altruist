/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Manages spatial zones in a 3D world.
    /// Validates that every zone lies inside the world (the area its partitions cover); a zone may span partitions.
    /// </summary>
    /// <remarks>
    /// Obtain it through <see cref="IGameWorldManager3D.Zones"/> (one per world, created lazily); it is not a DI service.
    /// Lookups are linear scans over registered zones, bounds are half-open <c>[Position, Position + Size)</c> in integer
    /// world units and inactive zones are skipped. Not thread-safe. For the 2D variant see <see cref="Altruist.Gaming.TwoD.ZoneManager2D"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// world.Zones.RegisterZone(new Zone3D("arena", new IntVector3(0, 0, 0), new IntVector3(32, 16, 32)));
    /// var zone = world.Zones.FindZoneAt(10, 2, 10); // "arena"
    /// </code>
    /// </example>
    public sealed class ZoneManager3D : IZoneManager3D
    {
        private readonly Dictionary<string, IZone3D> _zones = new(StringComparer.Ordinal);
        private readonly List<WorldPartitionManager3D> _partitions;

        /// <summary>Creates a zone manager validating against the world its partitions cover.</summary>
        /// <param name="partitions">The world's partitions (live list); together they are the area a zone must lie inside.</param>
        public ZoneManager3D(List<WorldPartitionManager3D> partitions)
        {
            _partitions = partitions;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is <c>null</c>.</exception>
        /// <exception cref="ZoneValidationException">Empty or duplicate name, a size that is not positive, or a zone reaching outside the world.</exception>
        public IZone3D RegisterZone(IZone3D zone)
        {
            if (zone is null)
                throw new ArgumentNullException(nameof(zone));

            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ZoneValidationException("Zone name cannot be empty.");

            if (_zones.ContainsKey(zone.Name))
                throw new ZoneValidationException($"Zone '{zone.Name}' is already registered.");

            if (zone.Size.X <= 0 || zone.Size.Y <= 0 || zone.Size.Z <= 0)
                throw new ZoneValidationException($"Zone '{zone.Name}' size ({zone.Size}) must be positive on every axis.");

            if (!IsInsideWorld(zone))
            {
                throw new ZoneValidationException(
                    $"Zone '{zone.Name}' at {zone.Position} with size {zone.Size} reaches outside the world.");
            }

            _zones[zone.Name] = zone;
            return zone;
        }

        /// <inheritdoc/>
        public IZone3D? GetZone(string name)
            => _zones.TryGetValue(name, out var zone) ? zone : null;

        /// <inheritdoc/>
        public bool RemoveZone(string name)
            => _zones.Remove(name);

        /// <inheritdoc/>
        public IEnumerable<IZone3D> GetAllZones()
            => _zones.Values;

        /// <inheritdoc/>
        /// <remarks>Returns the first active match (dictionary enumeration order, normally registration order) when zones overlap.</remarks>
        public IZone3D? FindZoneAt(int x, int y, int z)
        {
            foreach (var zone in _zones.Values)
            {
                if (!zone.IsActive) continue;

                if (x >= zone.Position.X && x < zone.Position.X + zone.Size.X &&
                    y >= zone.Position.Y && y < zone.Position.Y + zone.Size.Y &&
                    z >= zone.Position.Z && z < zone.Position.Z + zone.Size.Z)
                {
                    return zone;
                }
            }
            return null;
        }

        /// <inheritdoc/>
        /// <remarks>Strict overlap test: boxes that only touch are not returned.</remarks>
        public IEnumerable<IZone3D> FindZonesInBounds(
            int minX, int minY, int minZ,
            int maxX, int maxY, int maxZ)
        {
            var result = new List<IZone3D>();

            foreach (var zone in _zones.Values)
            {
                if (!zone.IsActive) continue;

                int zMaxX = zone.Position.X + zone.Size.X;
                int zMaxY = zone.Position.Y + zone.Size.Y;
                int zMaxZ = zone.Position.Z + zone.Size.Z;

                if (maxX > zone.Position.X && minX < zMaxX &&
                    maxY > zone.Position.Y && minY < zMaxY &&
                    maxZ > zone.Position.Z && minZ < zMaxZ)
                {
                    result.Add(zone);
                }
            }

            return result;
        }

        /// <summary>
        /// Whether the zone lies inside the box the partitions cover together. The partitions tile the world
        /// without gaps, so that box is the world.
        /// </summary>
        private bool IsInsideWorld(IZone3D zone)
        {
            if (_partitions.Count == 0)
                return false;

            int minX = _partitions.Min(p => p.Position.X);
            int minY = _partitions.Min(p => p.Position.Y);
            int minZ = _partitions.Min(p => p.Position.Z);
            int maxX = _partitions.Max(p => p.Position.X + p.Size.X);
            int maxY = _partitions.Max(p => p.Position.Y + p.Size.Y);
            int maxZ = _partitions.Max(p => p.Position.Z + p.Size.Z);

            return zone.Position.X >= minX && zone.Position.X + zone.Size.X <= maxX &&
                   zone.Position.Y >= minY && zone.Position.Y + zone.Size.Y <= maxY &&
                   zone.Position.Z >= minZ && zone.Position.Z + zone.Size.Z <= maxZ;
        }
    }
}
