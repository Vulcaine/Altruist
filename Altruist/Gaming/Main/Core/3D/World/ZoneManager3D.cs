/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Manages spatial zones in a 3D world.
    /// Validates that every zone fits entirely inside a single partition.
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
        private readonly IWorldPartitioner3D _partitioner;
        private readonly List<WorldPartitionManager3D> _partitions;

        /// <summary>Creates a zone manager validating against the given partition grid.</summary>
        /// <param name="partitioner">Partitioner providing the maximum zone size.</param>
        /// <param name="partitions">The world's partitions (live list) used for containment checks.</param>
        public ZoneManager3D(
            IWorldPartitioner3D partitioner,
            List<WorldPartitionManager3D> partitions)
        {
            _partitioner = partitioner;
            _partitions = partitions;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is <c>null</c>.</exception>
        /// <exception cref="ZoneValidationException">Empty or duplicate name, zone larger than a partition, or not inside a single partition.</exception>
        public IZone3D RegisterZone(IZone3D zone)
        {
            if (zone is null)
                throw new ArgumentNullException(nameof(zone));

            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ZoneValidationException("Zone name cannot be empty.");

            if (_zones.ContainsKey(zone.Name))
                throw new ZoneValidationException($"Zone '{zone.Name}' is already registered.");

            // Validate: zone cannot be larger than one partition
            if (zone.Size.X > _partitioner.PartitionWidth ||
                zone.Size.Y > _partitioner.PartitionHeight ||
                zone.Size.Z > _partitioner.PartitionDepth)
            {
                throw new ZoneValidationException(
                    $"Zone '{zone.Name}' size ({zone.Size}) exceeds partition size " +
                    $"({_partitioner.PartitionWidth}x{_partitioner.PartitionHeight}x{_partitioner.PartitionDepth}). " +
                    $"A zone must fit inside a single partition.");
            }

            // Validate: zone must be contained entirely within one partition
            var containing = FindContainingPartition(zone);
            if (containing is null)
            {
                throw new ZoneValidationException(
                    $"Zone '{zone.Name}' at {zone.Position} with size {zone.Size} " +
                    $"does not fit entirely inside any single partition.");
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
        /// Finds the partition that fully contains the zone, or null if none does.
        /// </summary>
        private WorldPartitionManager3D? FindContainingPartition(IZone3D zone)
        {
            int zMaxX = zone.Position.X + zone.Size.X;
            int zMaxY = zone.Position.Y + zone.Size.Y;
            int zMaxZ = zone.Position.Z + zone.Size.Z;

            foreach (var partition in _partitions)
            {
                int pMaxX = partition.Position.X + partition.Size.X;
                int pMaxY = partition.Position.Y + partition.Size.Y;
                int pMaxZ = partition.Position.Z + partition.Size.Z;

                if (zone.Position.X >= partition.Position.X && zMaxX <= pMaxX &&
                    zone.Position.Y >= partition.Position.Y && zMaxY <= pMaxY &&
                    zone.Position.Z >= partition.Position.Z && zMaxZ <= pMaxZ)
                {
                    return partition;
                }
            }

            return null;
        }
    }
}
