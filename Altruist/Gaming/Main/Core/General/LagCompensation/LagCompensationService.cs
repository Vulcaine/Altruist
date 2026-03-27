/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Engine;
using Altruist.Gaming.ThreeD;

namespace Altruist.Gaming;

/// <summary>
/// Server-side lag compensation: records per-entity position history and provides
/// temporal rewind for validation. Opt-in via config. Any module can use this —
/// combat, movement, interaction, abilities.
///
/// Uses an override map during rewind — entity positions are never mutated.
/// Consumers call GetCompensatedPosition() to read rewound positions.
/// </summary>
[Service(typeof(ILagCompensationService))]
[Service(typeof(IPositionHistoryRecorder))]
[ConditionalOnConfig("altruist:game:lag-compensation")]
public sealed class LagCompensationService : ILagCompensationService
{
    private readonly Dictionary<uint, EntityPositionHistory> _histories = new();
    private readonly Dictionary<uint, PositionSnapshot> _overrides = new();
    private readonly int _maxTicks;
    private readonly IGameWorldOrganizer3D? _worldOrganizer;

    public int HistoryDepthTicks => _maxTicks;
    public bool IsRewound { get; private set; }

    public LagCompensationService(
        [AppConfigValue("altruist:game:lag-compensation:history-ticks", "64")] int historyTicks = 64,
        IGameWorldOrganizer3D? worldOrganizer = null)
    {
        _maxTicks = Math.Max(1, historyTicks);
        _worldOrganizer = worldOrganizer;
    }

    public void RecordSnapshot(long tick)
    {
        if (_worldOrganizer == null) return;

        foreach (var world in _worldOrganizer.GetAllWorlds())
        {
            foreach (var obj in world.FindAllObjects<IWorldObject3D>())
            {
                var pos = obj.Transform.Position;
                if (!_histories.TryGetValue(obj.VirtualId, out var history))
                {
                    history = new EntityPositionHistory(_maxTicks);
                    _histories[obj.VirtualId] = history;
                }

                history.Record(tick, pos.X, pos.Y, pos.Z);
            }
        }
    }

    public void RewindWorld(long toTick, Action callback)
    {
        var currentTick = AltruistEngine.CurrentTick;
        var minTick = currentTick - _maxTicks;
        var clampedTick = Math.Clamp(toTick, minTick, currentTick);

        _overrides.Clear();
        foreach (var (vid, history) in _histories)
        {
            var snapshot = history.GetNearest(clampedTick);
            if (snapshot.HasValue)
                _overrides[vid] = snapshot.Value;
        }

        IsRewound = true;
        try
        {
            callback();
        }
        finally
        {
            IsRewound = false;
            _overrides.Clear();
        }
    }

    public (float X, float Y, float Z)? GetPositionAtTick(uint virtualId, long tick)
    {
        if (!_histories.TryGetValue(virtualId, out var history))
            return null;

        var snapshot = history.GetNearest(tick);
        if (!snapshot.HasValue)
            return null;

        return (snapshot.Value.X, snapshot.Value.Y, snapshot.Value.Z);
    }

    public (float X, float Y, float Z) GetCompensatedPosition(uint virtualId, float currentX, float currentY, float currentZ)
    {
        if (IsRewound && _overrides.TryGetValue(virtualId, out var snap))
            return (snap.X, snap.Y, snap.Z);

        return (currentX, currentY, currentZ);
    }

    public void RemoveEntity(uint virtualId)
    {
        _histories.Remove(virtualId);
    }
}
