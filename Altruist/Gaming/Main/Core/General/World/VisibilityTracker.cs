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
    /// Tracks which world objects are visible to each explicitly registered observer.
    /// On each tick, computes the diff between previously visible and currently visible objects,
    /// then fires events for enter/leave.
    ///
    /// Observer status is explicit: call Observe(worldObject). A non-empty ClientId is still
    /// required as the network destination, but ClientId alone does not make an object an observer.
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
        /// Registers a world object as a visibility observer.
        /// The object must be a dimension-specific world object and must have a non-empty ClientId.
        /// </summary>
        bool Observe(ITypelessWorldObject observer);

        /// <summary>
        /// Removes a registered observer world object.
        /// All previously visible objects will fire OnEntityInvisible.
        /// </summary>
        void RemoveObserver(ITypelessWorldObject observer);

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

        /// <summary>
        /// Returns the client IDs of all observers (players) who can see
        /// the given entity. Used for spatial sync broadcasting.
        /// </summary>
        IEnumerable<string> GetObserversOf(string entityInstanceId);

        /// <summary>
        /// Returns the world objects currently registered as visibility observers.
        /// </summary>
        IEnumerable<ITypelessWorldObject> GetObservers();
    }
}
