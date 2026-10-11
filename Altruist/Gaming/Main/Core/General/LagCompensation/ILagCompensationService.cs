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
/// Configure:
///   altruist:game:lag-compensation:history-ticks (default 64)
///   altruist:game:lag-compensation:snapshot-strategy = nearest | interpolate (default nearest)
///
/// <para>Ticks are engine frames (<c>AltruistEngine.CurrentTick</c>); history is recorded by the 3D world
/// organizer after each world step, keyed by the 3D world object's <c>VirtualId</c>. Not thread-safe:
/// rewind and record on the engine/world thread. The service is optional (registered only when the
/// config key exists): inject it nullable and use <see cref="LagCompensationExtensions.RewindOrRun(ILagCompensationService, long, Action)"/>,
/// which simply runs the action when compensation is off.</para>
///
/// <para>Choosing: use this for server-authoritative hit validation in persistent 3D worlds (melee sweeps,
/// hitscan) against what the client saw. Deterministic room simulations with client prediction usually
/// don't need it (inputs are applied at their own step instead).</para>
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
    /// Temporarily rewind all tracked entity positions to the given tick,
    /// execute the callback, return its result, then restore.
    /// </summary>
    T RewindWorld<T>(long toTick, Func<T> callback);

    /// <summary>
    /// Position pass-through transformer. During a RewindWorld callback, returns
    /// the historical position for the entity. Outside rewind, returns the input
    /// position unchanged. Use this in distance checks, sweep geometry, etc.
    /// </summary>
    (float X, float Y, float Z) Compensate(uint virtualId, float x, float y, float z);

    /// <summary>
    /// Yaw pass-through transformer. During a RewindWorld callback, returns the
    /// historical yaw (Y-axis rotation in radians) for the entity; outside rewind,
    /// returns <paramref name="currentYaw"/> unchanged. Use this alongside
    /// <see cref="Compensate"/> in facing-cone checks so the swing is validated
    /// against where the attacker actually faced at the rewound tick, not where
    /// they face now.
    /// </summary>
    float CompensateYaw(uint virtualId, float currentYaw);

    /// <summary>
    /// Remove all position history for an entity (call on destroy/despawn).
    /// </summary>
    void RemoveEntity(uint virtualId);

    /// <summary>How many ticks of history are retained per entity.</summary>
    int HistoryDepthTicks { get; }

    /// <summary>True while inside a RewindWorld callback.</summary>
    bool IsRewound { get; }
}
