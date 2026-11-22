using System.Numerics;
using FluentAssertions;

namespace Tests.Altruist.Numerics;

/// <summary>Bit-for-bit comparison and input generators for the deterministic helpers. Each
/// helper is checked against the inline expression it replaces, over edge values and random
/// inputs: the helper must give the same bits.</summary>
internal static class BitExact
{
    public static void Same(float actual, float expected) =>
        BitConverter.SingleToInt32Bits(actual).Should().Be(BitConverter.SingleToInt32Bits(expected),
            $"helper gave {actual:R}, inline gave {expected:R}");

    public static void Same(Vector2 actual, Vector2 expected)
    {
        Same(actual.X, expected.X);
        Same(actual.Y, expected.Y);
    }

    public static readonly float[] Edges =
    {
        0f, -0f, 1f, -1f, 0.5f, -0.5f, MathF.PI, -MathF.PI, MathF.PI / 2, 2 * MathF.PI, -2 * MathF.PI,
        3 * MathF.PI, 1e-7f, -1e-7f, 123.456f, -98765.4f, 0.49999997f,
    };

    /// <summary>Edge values, then <paramref name="count"/> random values in ±<paramref name="range"/>.</summary>
    public static IEnumerable<float> Floats(int count = 2000, float range = 50f, int seed = 1)
    {
        foreach (var e in Edges) yield return e;
        var r = new Random(seed);
        for (var i = 0; i < count; i++) yield return (float)(r.NextDouble() * 2 - 1) * range;
    }

    public static IEnumerable<Vector2> Vectors(int count = 2000, float range = 50f, int seed = 2)
    {
        yield return Vector2.Zero;
        yield return new Vector2(-0f, 0f);
        yield return Vector2.UnitX;
        yield return -Vector2.UnitY;
        var r = new Random(seed);
        for (var i = 0; i < count; i++)
            yield return new Vector2((float)(r.NextDouble() * 2 - 1) * range, (float)(r.NextDouble() * 2 - 1) * range);
    }

    public static IEnumerable<Vector2> Units(int count = 500, int seed = 3)
    {
        yield return Vector2.UnitX;
        yield return Vector2.UnitY;
        yield return -Vector2.UnitY;
        var r = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var a = (float)(r.NextDouble() * 2 * Math.PI);
            yield return new Vector2(MathF.Cos(a), MathF.Sin(a));
        }
    }
}
