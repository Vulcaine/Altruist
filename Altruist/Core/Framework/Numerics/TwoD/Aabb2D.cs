/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>Math layer. An axis-aligned rectangle given by its bounds (inclusive): trigger zones, camera
/// bounds, goal mouths. Contains / center are exact (deterministic: same bits on the same
/// runtime).</summary>
public readonly record struct Aabb2D(float MinX, float MaxX, float MinY, float MaxY)
{
    /// <summary>From two corners in any order.</summary>
    public static Aabb2D FromCorners(Vector2 a, Vector2 b) =>
        new(MathF.Min(a.X, b.X), MathF.Max(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.Y, b.Y));

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;

    /// <summary><c>((MinX + MaxX) / 2, (MinY + MaxY) / 2)</c>.</summary>
    public Vector2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    /// <summary><c>x &gt;= MinX &amp;&amp; x &lt;= MaxX &amp;&amp; y &gt;= MinY &amp;&amp; y &lt;= MaxY</c>.</summary>
    public bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    public bool Contains(Vector2 p) => Contains(p.X, p.Y);

    /// <summary>Contains with the box grown by a margin on each side (negative shrinks):
    /// <c>x &gt;= MinX - marginX &amp;&amp; x &lt;= MaxX + marginX &amp;&amp; y &gt;= MinY - marginY &amp;&amp; y &lt;= MaxY + marginY</c>.</summary>
    public bool Contains(float x, float y, float marginX, float marginY) =>
        x >= MinX - marginX && x <= MaxX + marginX && y >= MinY - marginY && y <= MaxY + marginY;

    /// <summary>Overlap test (touching edges count).</summary>
    public bool Intersects(Aabb2D other) =>
        MinX <= other.MaxX && MaxX >= other.MinX && MinY <= other.MaxY && MaxY >= other.MinY;
}
