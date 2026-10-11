/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

namespace Altruist.Dashboard
{
    /// <summary>
    /// The worlds of the running game as the dashboard shows them, whatever their dimension: one implementation reads
    /// the 3D worlds (<see cref="DashboardWorldSource3D"/>), one the 2D worlds (<see cref="DashboardWorldSource2D"/>);
    /// the one matching <c>altruist:environment:mode</c> is registered.
    /// </summary>
    /// <remarks>
    /// <see cref="Snapshot"/> and <see cref="Partitions"/> read the world from a request thread, as the world API allows
    /// for reads; <see cref="LiveObjects"/> is called by <see cref="DashboardWorldFeed"/> on the engine thread.
    /// </remarks>
    public interface IDashboardWorldSource
    {
        /// <summary>2 or 3.</summary>
        int Dimensions { get; }

        /// <summary>Every world, ordered by index.</summary>
        IReadOnlyList<WorldSummaryDto> Worlds();

        /// <summary>Whether a world with this index exists.</summary>
        bool Exists(int worldIndex);

        /// <summary>The world's non-empty partitions with their objects; empty for an unknown world.</summary>
        IEnumerable<WorldPartitionObjectsDto> Partitions(int worldIndex);

        /// <summary>Every object the dashboard shows, in full (colliders included).</summary>
        IEnumerable<WorldObjectDto> Objects(int worldIndex);

        /// <summary>Where each shown object is now: the cheap per-tick view the live feed diffs.</summary>
        IEnumerable<DashboardObjectPoseDto> LiveObjects(int worldIndex);

        /// <summary>One shown object in full, or null.</summary>
        WorldObjectDto? Object(int worldIndex, string instanceId);

        /// <summary>Gizmos the source itself adds (visibility radii); the registry's own come from <see cref="IDashboardGizmoRegistry"/>.</summary>
        IEnumerable<DashboardGizmo> SourceGizmos(int worldIndex);
    }

    /// <summary>Visibility radii shared by both sources: one circle per observer that is in the world.</summary>
    internal static class DashboardVisibilityGizmos
    {
        public static string IdOf(string observerId) => $"altruist:visibility:observer:{observerId}:radius";

        public static DashboardGizmo Of(int worldIndex, string observerId, Vector3Dto position, float range) => new()
        {
            Id = IdOf(observerId),
            WorldIndex = worldIndex,
            Category = "visibility",
            Source = "altruist",
            Type = "circle",
            Label = $"visibility radius {range:F0}",
            Color = "#22C55E66",
            Position = position,
            Radius = range,
            AttachToInstanceId = observerId,
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }
}
