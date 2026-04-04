/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Immutable position record at a specific engine tick.
/// Stored in pre-allocated ring buffers — zero allocation during gameplay.
/// </summary>
public readonly struct PositionSnapshot
{
    public readonly long Tick;
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public PositionSnapshot(long tick, float x, float y, float z)
    {
        Tick = tick;
        X = x;
        Y = y;
        Z = z;
    }
}
