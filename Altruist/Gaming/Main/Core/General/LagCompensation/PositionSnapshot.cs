/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Immutable position + yaw record at a specific engine tick.
/// Stored in pre-allocated ring buffers — zero allocation during gameplay.
/// Yaw is Y-axis rotation in radians; for entities without meaningful orientation, 0.
/// </summary>
public readonly struct PositionSnapshot
{
    /// <summary>Engine tick the snapshot was taken at.</summary>
    public readonly long Tick;
    /// <summary>World X.</summary>
    public readonly float X;
    /// <summary>World Y.</summary>
    public readonly float Y;
    /// <summary>World Z.</summary>
    public readonly float Z;
    /// <summary>Y-axis rotation in radians.</summary>
    public readonly float Yaw;

    /// <summary>Creates a snapshot.</summary>
    public PositionSnapshot(long tick, float x, float y, float z, float yaw)
    {
        Tick = tick;
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
    }
}
