/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>A side of a box in its own facing terms: <see cref="Front"/> is local +X (after the
/// caller has turned X to the facing), <see cref="Top"/> local +Y. See
/// <see cref="Geometry2D.ClassifyBoxSide"/>.</summary>
public enum BoxSide2D
{
    Front,
    Back,
    Top,
    Bottom,
}

/// <summary>Math layer. Point-versus-shape tests for boxes and rays. Box tests take the point in the box's
/// local frame (centered, axis-aligned), e.g. from <c>body.GetLocalPoint(worldPoint)</c>; the
/// body extensions in <c>Altruist.Physx.TwoD</c> (<c>BodyMotionExtensions2D.BoxContains</c>,
/// <c>BoxDistanceSquared</c>) do that conversion.
/// <para>Deterministic: same inputs give the same bits on the same runtime. Each helper evaluates
/// exactly the expression in its summary.</para></summary>
public static class Geometry2D
{
    /// <summary>Is the local point inside (or on) the centered box with these half extents?
    /// <c>!(MathF.Abs(local.X) &gt; halfWidth || MathF.Abs(local.Y) &gt; halfHeight)</c>.
    /// Pass enlarged half extents for a padded test.</summary>
    public static bool BoxContainsLocal(Vector2 local, float halfWidth, float halfHeight) =>
        !(MathF.Abs(local.X) > halfWidth || MathF.Abs(local.Y) > halfHeight);

    /// <summary>Squared distance from the local point to the centered box (0 inside):
    /// <c>dx = MathF.Max(0, |x| - halfWidth); dy = MathF.Max(0, |y| - halfHeight); dx * dx + dy * dy</c>.
    /// Compare it with a squared radius to avoid the square root.</summary>
    public static float BoxDistanceSquaredLocal(Vector2 local, float halfWidth, float halfHeight)
    {
        var dx = MathF.Max(0, MathF.Abs(local.X) - halfWidth);
        var dy = MathF.Max(0, MathF.Abs(local.Y) - halfHeight);
        return dx * dx + dy * dy;
    }

    /// <summary>Half extent along the frame's Y axis of a box rotated by
    /// <paramref name="angle"/> relative to the frame (how far it reaches up and down):
    /// <c>halfWidth * |sin(angle)| + halfHeight * |cos(angle)|</c>.</summary>
    public static float RotatedBoxHalfExtentY(float halfWidth, float halfHeight, float angle) =>
        halfWidth * MathF.Abs(MathF.Sin(angle)) + halfHeight * MathF.Abs(MathF.Cos(angle));

    /// <summary>Half extent along the frame's X axis of a box rotated by
    /// <paramref name="angle"/>: <c>halfWidth * |cos(angle)| + halfHeight * |sin(angle)|</c>.</summary>
    public static float RotatedBoxHalfExtentX(float halfWidth, float halfHeight, float angle) =>
        halfWidth * MathF.Abs(MathF.Cos(angle)) + halfHeight * MathF.Abs(MathF.Sin(angle));

    /// <summary>Which side of the centered box a local point (or local direction) is on. The
    /// coordinates are scaled by the half extents (the box becomes a unit square) and the dominant
    /// axis wins; <paramref name="axisBias"/> &gt; 1 favors front / back on the corners, &lt; 1 top /
    /// bottom:
    /// <code>
    /// nx = local.X / halfWidth; ny = local.Y / halfHeight
    /// MathF.Abs(nx) * axisBias &gt;= MathF.Abs(ny) ? (nx &gt;= 0 ? Front : Back) : (ny &gt;= 0 ? Top : Bottom)
    /// </code>
    /// For a body that can face either way pass <c>(local.X * facing, local.Y)</c> (front = the facing
    /// side): <c>(local.X * facing) / halfWidth</c> is the same bits as <c>local.X * facing / halfWidth</c>.</summary>
    public static BoxSide2D ClassifyBoxSide(Vector2 local, float halfWidth, float halfHeight, float axisBias)
    {
        var nx = local.X / halfWidth;
        var ny = local.Y / halfHeight;
        if (MathF.Abs(nx) * axisBias >= MathF.Abs(ny)) return nx >= 0 ? BoxSide2D.Front : BoxSide2D.Back;
        return ny >= 0 ? BoxSide2D.Top : BoxSide2D.Bottom;
    }

    /// <summary>Where <paramref name="point"/> lies relative to the ray from
    /// <paramref name="origin"/> along the unit <paramref name="direction"/>: the distance
    /// <c>Along</c> the ray (negative = behind the origin) and the perpendicular distance
    /// <c>Offset</c> (≥ 0) from the ray's line:
    /// <c>Along = (p.X - o.X) * d.X + (p.Y - o.Y) * d.Y</c>,
    /// <c>Offset = MathF.Abs((p.X - o.X) * d.Y - (p.Y - o.Y) * d.X)</c>.</summary>
    public static (float Along, float Offset) PointToRay(Vector2 origin, Vector2 direction, Vector2 point)
    {
        var along = (point.X - origin.X) * direction.X + (point.Y - origin.Y) * direction.Y;
        var offset = MathF.Abs((point.X - origin.X) * direction.Y - (point.Y - origin.Y) * direction.X);
        return (along, offset);
    }
}
