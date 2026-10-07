/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Numerics;

/// <summary>Math layer. "One number at a time" — clamp, step toward, interpolate, sign, round,
/// quantize. Pure float32 functions, dimension-agnostic.
/// <para>Deterministic: same inputs give the same bits on the same runtime. Every helper
/// evaluates exactly the expression in its summary (same operands, same grouping, no fused
/// multiply-add), so it can replace that expression written inline without changing a bit.
/// The TypeScript package <c>@altruist/sim2d</c> has the same functions (camelCase) with the
/// same operation order on JavaScript numbers.</para></summary>
public static class Scalar
{
    /// <summary><c>v &lt; min ? min : v &gt; max ? max : v</c>. Never throws (unlike
    /// <see cref="Math.Clamp(float,float,float)"/> when <paramref name="min"/> &gt; <paramref name="max"/>);
    /// NaN stays NaN.</summary>
    public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    /// <summary><see cref="Clamp"/> to [0, 1].</summary>
    public static float Clamp01(float v) => Clamp(v, 0f, 1f);

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> by at most
    /// <paramref name="maxDelta"/> without overshooting:
    /// <c>current &lt; target ? MathF.Min(current + maxDelta, target) : MathF.Max(current - maxDelta, target)</c>.
    /// <paramref name="maxDelta"/> is expected to be ≥ 0 (not guarded, so the expression stays exact).</summary>
    public static float Approach(float current, float target, float maxDelta) =>
        current < target ? MathF.Min(current + maxDelta, target) : MathF.Max(current - maxDelta, target);

    /// <summary>Linear interpolation <c>a + (b - a) * t</c> (unclamped).</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Where <paramref name="v"/> lies between <paramref name="a"/> and <paramref name="b"/>:
    /// <c>(v - a) / (b - a)</c> (unclamped; ±∞ or NaN when a == b).</summary>
    public static float InverseLerp(float a, float b, float v) => (v - a) / (b - a);

    /// <summary>Maps <paramref name="v"/> from [inMin, inMax] to [outMin, outMax] (unclamped):
    /// <c>Lerp(outMin, outMax, InverseLerp(inMin, inMax, v))</c>.</summary>
    public static float Remap(float v, float inMin, float inMax, float outMin, float outMax) =>
        Lerp(outMin, outMax, InverseLerp(inMin, inMax, v));

    /// <summary>A power ease on [0, 1]: <c>MathF.Pow(Clamp01(t), exponent)</c>. Exponent 1 is
    /// linear, above 1 starts slow, below 1 starts fast.</summary>
    public static float Pow01(float t, float exponent) => MathF.Pow(Clamp01(t), exponent);

    /// <summary><c>v &gt; 0 ? 1 : v &lt; 0 ? -1 : 0</c> as a float. Unlike
    /// <see cref="MathF.Sign(float)"/> it never throws: NaN gives 0.</summary>
    public static float Sign(float v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    /// <summary><c>v &gt; 0 ? 1 : v &lt; 0 ? -1 : fallback</c>: the sign, or
    /// <paramref name="fallback"/> for zero (and NaN).</summary>
    public static int SignOr(float v, int fallback = 0) => v > 0 ? 1 : v < 0 ? -1 : fallback;

    /// <summary>Rounds halves up: <c>MathF.Floor(v + 0.5f)</c>. Agrees with JavaScript's
    /// <c>Math.round</c> (ties toward +∞) except right below 0.5, where <c>v + 0.5f</c> rounds up
    /// to the next integer in float32 (e.g. 0.49999997f gives 1). Use it to reproduce
    /// <c>Floor(v + 0.5)</c> code; for "nearest, ties away from zero" use
    /// <c>MathF.Round(v, MidpointRounding.AwayFromZero)</c>.</summary>
    public static float RoundHalfUp(float v) => MathF.Floor(v + 0.5f);

    /// <summary>Quantizes a value in [-1, 1] to <paramref name="steps"/> steps per unit (127: a signed
    /// byte): <c>MathF.Round(Clamp(v, -1, 1) * steps) / steps</c>. <see cref="MathF.Round(float)"/>
    /// rounds ties to even; the TypeScript twin uses <c>Math.round</c> (ties up), so the two differ
    /// only for inputs exactly halfway between two steps.</summary>
    public static float Quantize(float v, int steps = 127) => MathF.Round(Clamp(v, -1f, 1f) * steps) / steps;
}
