/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming
{
    /// <summary>
    /// Event args for visibility changes detected by the tracker.
    /// </summary>
    public readonly struct VisibilityChange
    {
        /// <summary>The client connection ID of the observer (the player who sees/unsees).</summary>
        public string ObserverClientId { get; init; }

        /// <summary>The world object that entered or left the observer's view.</summary>
        public ITypelessWorldObject Target { get; init; }

        /// <summary>The world index where this visibility change occurred.</summary>
        public int WorldIndex { get; init; }
    }

    /// <summary>
    /// Tracks which world objects are visible to each observer (client-linked object).
    /// On each tick, computes the diff between previously visible and currently visible objects,
    /// then fires events for enter/leave.
    ///
    /// Observers are world objects with a non-empty ClientId.
    /// </summary>
    public interface IVisibilityTracker
    {
        /// <summary>View range radius for visibility queries.</summary>
        float ViewRange { get; set; }

        /// <summary>Fired when a world object enters an observer's view range.</summary>
        event Action<VisibilityChange> OnEntityVisible;

        /// <summary>Fired when a world object leaves an observer's view range.</summary>
        event Action<VisibilityChange> OnEntityInvisible;

        /// <summary>
        /// Forces a full visibility refresh for a specific observer.
        /// Call this when a player first enters the world or teleports.
        /// All currently nearby objects will fire OnEntityVisible.
        /// </summary>
        void RefreshObserver(string clientId);

        /// <summary>
        /// Removes all tracking state for an observer (e.g. on disconnect).
        /// All previously visible objects will fire OnEntityInvisible.
        /// </summary>
        void RemoveObserver(string clientId);

        /// <summary>
        /// Returns the set of instance IDs currently visible to an observer.
        /// </summary>
        IReadOnlySet<string>? GetVisibleEntities(string clientId);
    }
}
