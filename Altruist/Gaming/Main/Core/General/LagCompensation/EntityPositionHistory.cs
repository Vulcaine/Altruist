/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Fixed-size ring buffer storing position snapshots for a single entity.
/// Pre-allocated array — no allocations after construction.
/// Default capacity of 64 entries covers ~2.5 seconds at 25Hz.
/// </summary>
public sealed class EntityPositionHistory
{
    private readonly PositionSnapshot[] _buffer;
    private int _head;
    private int _count;

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public EntityPositionHistory(int capacity = 64)
    {
        _buffer = new PositionSnapshot[capacity];
    }

    public void Record(long tick, float x, float y, float z, float yaw)
    {
        _buffer[_head] = new PositionSnapshot(tick, x, y, z, yaw);
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length)
            _count++;
    }

    /// <summary>
    /// Get the position snapshot at the exact tick, or null if not found.
    /// </summary>
    public PositionSnapshot? GetAtTick(long tick)
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
            if (_buffer[idx].Tick == tick)
                return _buffer[idx];
            // Ring is ordered newest→oldest; if we've passed the target tick, stop
            if (_buffer[idx].Tick < tick)
                return null;
        }
        return null;
    }

    /// <summary>
    /// Get the closest snapshot to the requested tick.
    /// Returns the snapshot with the smallest absolute tick difference.
    /// </summary>
    public PositionSnapshot? GetNearest(long tick)
    {
        if (_count == 0)
            return null;

        int bestIdx = (_head - 1 + _buffer.Length) % _buffer.Length;
        long bestDiff = Math.Abs(_buffer[bestIdx].Tick - tick);

        for (int i = 1; i < _count; i++)
        {
            int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
            long diff = Math.Abs(_buffer[idx].Tick - tick);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = idx;
            }
            // Once we're moving further away, stop (ticks are monotonic)
            if (_buffer[idx].Tick < tick)
                break;
        }

        return _buffer[bestIdx];
    }

    /// <summary>
    /// Get an exact snapshot or interpolate between the nearest snapshots around the tick.
    /// Falls back to nearest when the requested tick cannot be bracketed.
    /// </summary>
    public PositionSnapshot? GetInterpolated(long tick)
    {
        if (_count == 0)
            return null;

        PositionSnapshot? before = null;
        PositionSnapshot? after = null;

        for (int i = 0; i < _count; i++)
        {
            int idx = (_head - 1 - i + _buffer.Length) % _buffer.Length;
            var snapshot = _buffer[idx];

            if (snapshot.Tick == tick)
                return snapshot;

            if (snapshot.Tick < tick)
            {
                if (!before.HasValue || snapshot.Tick > before.Value.Tick)
                    before = snapshot;
            }
            else
            {
                if (!after.HasValue || snapshot.Tick < after.Value.Tick)
                    after = snapshot;
            }
        }

        if (!before.HasValue || !after.HasValue)
            return GetNearest(tick);

        var a = before.Value;
        var b = after.Value;
        var tickSpan = b.Tick - a.Tick;
        if (tickSpan <= 0)
            return GetNearest(tick);

        var t = (float)(tick - a.Tick) / tickSpan;
        return new PositionSnapshot(
            tick,
            Lerp(a.X, b.X, t),
            Lerp(a.Y, b.Y, t),
            Lerp(a.Z, b.Z, t),
            LerpAngleRadians(a.Yaw, b.Yaw, t));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float LerpAngleRadians(float a, float b, float t)
    {
        var delta = NormalizeRadians(b - a);
        return NormalizeRadians(a + delta * t);
    }

    private static float NormalizeRadians(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.PI * 2f;
        while (angle < -MathF.PI)
            angle += MathF.PI * 2f;
        return angle;
    }
}
