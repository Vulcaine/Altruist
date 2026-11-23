/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>
    /// Manages spatial zones in a 2D world.
    /// Validates that every zone lies inside the world (the area its partitions cover); a zone may span partitions.
    /// <para>Not registered in DI: each world creates its own lazily, reachable as
    /// <see cref="IGameWorldManager2D.Zones"/>. Lookups skip inactive zones; when zones overlap,
    /// <see cref="FindZoneAt"/> returns the first match in registration order. Not thread-safe.</para>
    /// </summary>
    public sealed class ZoneManager2D : IZoneManager2D
    {
        private readonly Dictionary<string, IZone2D> _zones = new(StringComparer.Ordinal);
        private readonly List<WorldPartition2D> _partitions;

        /// <summary>Creates a zone manager over a world's partitions.</summary>
        /// <param name="partitions">The world's partitions (live list); together they are the area a zone must lie inside.</param>
        public ZoneManager2D(List<WorldPartition2D> partitions)
        {
            _partitions = partitions;
        }

        /// <summary>Registers <paramref name="zone"/> after validating it.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="zone"/> is null.</exception>
        /// <exception cref="ZoneValidationException">The name is empty or already used, a size is not positive,
        /// or the zone reaches outside the world.</exception>
        public IZone2D RegisterZone(IZone2D zone)
        {
            if (zone is null)
                throw new ArgumentNullException(nameof(zone));

            if (string.IsNullOrWhiteSpace(zone.Name))
                throw new ZoneValidationException("Zone name cannot be empty.");

            if (_zones.ContainsKey(zone.Name))
                throw new ZoneValidationException($"Zone '{zone.Name}' is already registered.");

            if (zone.Size.X <= 0 || zone.Size.Y <= 0)
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
        public IZone2D? GetZone(string name)
            => _zones.TryGetValue(name, out var zone) ? zone : null;

        /// <inheritdoc/>
        public bool RemoveZone(string name)
            => _zones.Remove(name);

        /// <inheritdoc/>
        public IEnumerable<IZone2D> GetAllZones()
            => _zones.Values;

        /// <summary>The first active zone containing (<paramref name="x"/>, <paramref name="y"/>)
        /// (min edge inclusive, max edge exclusive), or null.</summary>
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

        /// <summary>Active zones overlapping the rectangle [<paramref name="minX"/>, <paramref name="maxX"/>) x
        /// [<paramref name="minY"/>, <paramref name="maxY"/>) (touching edges do not count).</summary>
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
        /// Whether the zone lies inside the rectangle the partitions cover together. The partitions tile the
        /// world without gaps, so that rectangle is the world.
        /// </summary>
        private bool IsInsideWorld(IZone2D zone)
        {
            if (_partitions.Count == 0)
                return false;

            int minX = _partitions.Min(p => p.Position.X);
            int minY = _partitions.Min(p => p.Position.Y);
            int maxX = _partitions.Max(p => p.Position.X + p.Size.X);
            int maxY = _partitions.Max(p => p.Position.Y + p.Size.Y);

            return zone.Position.X >= minX && zone.Position.X + zone.Size.X <= maxX &&
                   zone.Position.Y >= minY && zone.Position.Y + zone.Size.Y <= maxY;
        }
    }
}
