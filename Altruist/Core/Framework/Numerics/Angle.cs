/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.ThreeD.Numerics;

/// <summary>"Angle bookkeeping" — wrap, signed difference, clamped step,
/// degree↔radian conversion. Pure functions over radians.</summary>
public static class Angle
{
    private const float TwoPi = MathF.PI * 2f;

    /// <summary>Wrap <paramref name="radians"/> into [-π, π].</summary>
    public static float Normalize(float radians)
    {
        radians = MathF.IEEERemainder(radians, TwoPi);
        if (radians > MathF.PI) radians -= TwoPi;
        else if (radians < -MathF.PI) radians += TwoPi;
        return radians;
    }

    /// <summary>Signed shortest delta from <paramref name="from"/> to
    /// <paramref name="to"/>. Positive = rotate in the +yaw direction.</summary>
    public static float ShortestDifference(float from, float to)
        => Normalize(to - from);

    /// <summary>Step <paramref name="current"/> toward <paramref name="target"/>
    /// by at most <paramref name="maxDelta"/> radians, taking the short way
    /// around. Never overshoots.</summary>
    public static float MoveToward(float current, float target, float maxDelta)
    {
        if (maxDelta <= 0f) return current;
        var diff = ShortestDifference(current, target);
        if (MathF.Abs(diff) <= maxDelta) return target;
        return Normalize(current + MathF.Sign(diff) * maxDelta);
    }

    public static float ToRadians(float degrees) => degrees * (MathF.PI / 180f);
    public static float ToDegrees(float radians) => radians * (180f / MathF.PI);
}
