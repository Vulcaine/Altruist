/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Engine;
using Altruist.Gaming.ThreeD;

namespace Altruist.Gaming;

/// <summary>
/// Server-side lag compensation: records per-entity position history and provides
/// temporal rewind. Opt-in via config. Any module can use this.
///
/// Uses an override map during rewind — entity positions are never mutated.
///
/// Entities are pushed in via <see cref="RecordSnapshot"/> each tick by the
/// world organizer; the service itself doesn't depend on the organizer, which
/// avoids a circular DI cycle when the Organizer declares an
/// <c>IPositionHistoryRecorder</c> dependency.
/// </summary>
[Service(typeof(ILagCompensationService))]
[Service(typeof(IPositionHistoryRecorder))]
[ConditionalOnConfig("altruist:game:lag-compensation")]
public sealed class LagCompensationService : ILagCompensationService
{
    private readonly Dictionary<uint, EntityPositionHistory> _histories = new();
    private readonly Dictionary<uint, PositionSnapshot> _overrides = new();
    private readonly int _maxTicks;
    private readonly LagCompensationSnapshotStrategy _snapshotStrategy;

    /// <inheritdoc/>
    public int HistoryDepthTicks => _maxTicks;
    /// <inheritdoc/>
    public bool IsRewound { get; private set; }

    /// <summary>Created by DI from <c>altruist:game:lag-compensation:*</c>.</summary>
    /// <param name="historyTicks">Snapshots kept per entity (minimum 1); rewinds further back are clamped.</param>
    /// <param name="snapshotStrategy">How a rewound tick between recorded snapshots is resolved.</param>
    public LagCompensationService(
        [AppConfigValue("altruist:game:lag-compensation:history-ticks", "64")] int historyTicks = 64,
        [AppConfigValue("altruist:game:lag-compensation:snapshot-strategy", "nearest")] LagCompensationSnapshotStrategy snapshotStrategy = LagCompensationSnapshotStrategy.Nearest)
    {
        _maxTicks = Math.Max(1, historyTicks);
        _snapshotStrategy = snapshotStrategy;
    }

    /// <summary>Appends the current position and yaw (<see cref="IHasFacingYaw"/> if implemented, else derived
    /// from the rotation) of every entity to its history. Called by the world organizer once per step.</summary>
    public void RecordSnapshot(long tick, IEnumerable<IWorldObject3D> entities)
    {
        if (entities == null) return;

        foreach (var obj in entities)
        {
            var pos = obj.Transform.Position;
            var yaw = ExtractYaw(obj);
            if (!_histories.TryGetValue(obj.VirtualId, out var history))
            {
                history = new EntityPositionHistory(_maxTicks);
                _histories[obj.VirtualId] = history;
            }

            history.Record(tick, pos.X, pos.Y, pos.Z, yaw);
        }
    }

    /// <summary>Pull yaw from the entity: prefer the explicit <see cref="IHasFacingYaw"/>
    /// accessor (e.g. player body yaw maintained outside Transform.Rotation),
    /// otherwise derive from the rotation quaternion.</summary>
    private static float ExtractYaw(IWorldObject3D obj)
    {
        if (obj is IHasFacingYaw yawProvider)
            return yawProvider.FacingYaw;

        var q = obj.Transform.Rotation.ToQuaternion();
        // Y-axis yaw from quaternion (forward = q * +Z): atan2(forward.x, forward.z).
        float fx = 2f * (q.X * q.Z + q.W * q.Y);
        float fz = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        return MathF.Atan2(fx, fz);
    }

    /// <inheritdoc/>
    public void RewindWorld(long toTick, Action callback)
    {
        RewindWorld<object?>(toTick, () =>
        {
            callback();
            return null;
        });
    }

    /// <summary>
    /// Clamps <paramref name="toTick"/> to <c>[CurrentTick - HistoryDepthTicks, CurrentTick]</c>, resolves each
    /// entity's snapshot there, runs <paramref name="callback"/> with <see cref="Compensate"/> /
    /// <see cref="CompensateYaw"/> returning those, and restores. Entity state is never mutated. Not re-entrant.
    /// </summary>
    public T RewindWorld<T>(long toTick, Func<T> callback)
    {
        var currentTick = AltruistEngine.CurrentTick;
        var minTick = currentTick - _maxTicks;
        var clampedTick = Math.Clamp(toTick, minTick, currentTick);

        _overrides.Clear();
        foreach (var (vid, history) in _histories)
        {
            var snapshot = _snapshotStrategy == LagCompensationSnapshotStrategy.Interpolate
                ? history.GetInterpolated(clampedTick)
                : history.GetNearest(clampedTick);
            if (snapshot.HasValue)
                _overrides[vid] = snapshot.Value;
        }

        IsRewound = true;
        try
        {
            return callback();
        }
        finally
        {
            IsRewound = false;
            _overrides.Clear();
        }
    }

    /// <inheritdoc/>
    public (float X, float Y, float Z) Compensate(uint virtualId, float x, float y, float z)
    {
        if (IsRewound && _overrides.TryGetValue(virtualId, out var snap))
            return (snap.X, snap.Y, snap.Z);

        return (x, y, z);
    }

    /// <inheritdoc/>
    public float CompensateYaw(uint virtualId, float currentYaw)
    {
        if (IsRewound && _overrides.TryGetValue(virtualId, out var snap))
            return snap.Yaw;

        return currentYaw;
    }

    /// <inheritdoc/>
    public void RemoveEntity(uint virtualId)
    {
        _histories.Remove(virtualId);
    }
}
