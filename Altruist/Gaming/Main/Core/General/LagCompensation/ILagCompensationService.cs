/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Server-side lag compensation service.
/// Records entity position history each tick and provides temporal rewind
/// for validation against historical positions.
///
/// Any module (combat, movement, interaction, abilities) can use this to
/// validate actions against where entities were at the client's perceived time.
///
/// Enable: set altruist:game:lag-compensation = true
/// Configure history depth: altruist:game:lag-compensation:history-ticks (default 64)
/// </summary>
public interface ILagCompensationService : IPositionHistoryRecorder
{
    /// <summary>
    /// Temporarily rewind all tracked entity positions to the given tick,
    /// execute the callback, then restore. Uses an override map internally —
    /// entity positions are never mutated. The callback should contain the
    /// actual validation/detection logic.
    /// </summary>
    void RewindWorld(long toTick, Action callback);

    /// <summary>
    /// Query a single entity's historical position at a specific tick.
    /// Returns null if the entity has no recorded history at that tick.
    /// </summary>
    (float X, float Y, float Z)? GetPositionAtTick(uint virtualId, long tick);

    /// <summary>
    /// Get the effective position of an entity — returns the rewound position
    /// if currently inside a RewindWorld callback, otherwise returns the
    /// provided current position unchanged.
    /// </summary>
    (float X, float Y, float Z) GetCompensatedPosition(uint virtualId, float currentX, float currentY, float currentZ);

    /// <summary>
    /// Remove all position history for an entity (call on destroy/despawn).
    /// </summary>
    void RemoveEntity(uint virtualId);

    /// <summary>How many ticks of history are retained per entity.</summary>
    int HistoryDepthTicks { get; }

    /// <summary>True while inside a RewindWorld callback.</summary>
    bool IsRewound { get; }
}
