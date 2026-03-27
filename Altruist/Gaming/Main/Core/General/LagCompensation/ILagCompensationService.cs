/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Server-side lag compensation service.
/// Records entity position history each tick and provides temporal rewind
/// so any module can validate actions against where entities were at the
/// client's perceived time.
///
/// Usage — any module can wrap its logic in RewindWorld:
///   _lagCompensation.RewindWorld(clientTick, () =>
///   {
///       // All Compensate() calls inside here return historical positions
///       var (x, y, z) = _lagCompensation.Compensate(entity.VirtualId, entity.X, entity.Y, entity.Z);
///   });
///
/// Enable: set altruist:game:lag-compensation = true
/// Configure: altruist:game:lag-compensation:history-ticks (default 64)
/// </summary>
public interface ILagCompensationService : IPositionHistoryRecorder
{
    /// <summary>
    /// Temporarily rewind all tracked entity positions to the given tick,
    /// execute the callback, then restore. Inside the callback, Compensate()
    /// returns historical positions. Outside, it passes through unchanged.
    /// </summary>
    void RewindWorld(long toTick, Action callback);

    /// <summary>
    /// Position pass-through transformer. During a RewindWorld callback, returns
    /// the historical position for the entity. Outside rewind, returns the input
    /// position unchanged. Use this in distance checks, sweep geometry, etc.
    /// </summary>
    (float X, float Y, float Z) Compensate(uint virtualId, float x, float y, float z);

    /// <summary>
    /// Remove all position history for an entity (call on destroy/despawn).
    /// </summary>
    void RemoveEntity(uint virtualId);

    /// <summary>How many ticks of history are retained per entity.</summary>
    int HistoryDepthTicks { get; }

    /// <summary>True while inside a RewindWorld callback.</summary>
    bool IsRewound { get; }
}
