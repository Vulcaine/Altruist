/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>
    /// Manages spatial zones in a 2D world.
    /// Validates that every zone fits entirely inside a single partition.
    /// </summary>
    public sealed class ZoneManager2D : IZoneManager2D
    {
        private readonly Dictionary<string, IZone2D> _zones = new(StringComparer.Ordinal);
        private readonly IWorldPartitioner2D _partitioner;
        private readonly List<WorldPartition2D> _partitions;

        public ZoneManager2D(
            IWorldPartitioner2D partitioner,
            List<WorldPartition2D> partitions)
        {
            _partitioner = partitioner;
            _partitions = partitions;
        }

        public IZone2D RegisterZone(IZone2D zone)
        {
            if (zone is null)
                throw new ArgumentNullException(nameof(zone));

            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ZoneValidationException("Zone name cannot be empty.");

            if (_zones.ContainsKey(zone.Name))
                throw new ZoneValidationException($"Zone '{zone.Name}' is already registered.");

            // Validate: zone cannot be larger than one partition
            if (zone.Size.X > _partitioner.PartitionWidth ||
                zone.Size.Y > _partitioner.PartitionHeight)
            {
                throw new ZoneValidationException(
                    $"Zone '{zone.Name}' size ({zone.Size}) exceeds partition size " +
                    $"({_partitioner.PartitionWidth}x{_partitioner.PartitionHeight}). " +
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

        public IZone2D? GetZone(string name)
            => _zones.TryGetValue(name, out var zone) ? zone : null;

        public bool RemoveZone(string name)
            => _zones.Remove(name);

        public IEnumerable<IZone2D> GetAllZones()
            => _zones.Values;

        public IZone2D? FindZoneAt(int x, int y)
        {
            foreach (var zone in _zones.Values)
            {
                if (!zone.IsActive) continue;

                if (x >= zone.Position.X && x < zone.Position.X + zone.Size.X &&
                    y >= zone.Position.Y && y < zone.Position.Y + zone.Size.Y)
                {
                    return zone;
                }
            }
            return null;
        }

        public IEnumerable<IZone2D> FindZonesInBounds(int minX, int minY, int maxX, int maxY)
        {
            var result = new List<IZone2D>();

            foreach (var zone in _zones.Values)
            {
                if (!zone.IsActive) continue;

                int zMaxX = zone.Position.X + zone.Size.X;
                int zMaxY = zone.Position.Y + zone.Size.Y;

                if (maxX > zone.Position.X && minX < zMaxX &&
                    maxY > zone.Position.Y && minY < zMaxY)
                {
                    result.Add(zone);
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the partition that fully contains the zone, or null if none does.
        /// </summary>
        private WorldPartition2D? FindContainingPartition(IZone2D zone)
        {
            int zMaxX = zone.Position.X + zone.Size.X;
            int zMaxY = zone.Position.Y + zone.Size.Y;

            foreach (var partition in _partitions)
            {
                int pMaxX = partition.Position.X + partition.Size.X;
                int pMaxY = partition.Position.Y + partition.Size.Y;

                if (zone.Position.X >= partition.Position.X && zMaxX <= pMaxX &&
                    zone.Position.Y >= partition.Position.Y && zMaxY <= pMaxY)
                {
                    return partition;
                }
            }

            return null;
        }
    }
}
