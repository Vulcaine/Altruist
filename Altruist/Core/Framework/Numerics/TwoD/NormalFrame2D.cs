/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>Which perpendicular of a normal a <see cref="NormalFrame2D"/> uses as its tangent.</summary>
public enum TangentSide2D
{
    /// <summary>90° counter-clockwise of the normal: <c>(-n.Y, n.X)</c> (<see cref="Direction2D.Perpendicular"/>).</summary>
    Left,

    /// <summary>90° clockwise of the normal: <c>(n.Y, -n.X)</c> (<see cref="Direction2D.PerpendicularClockwise"/>);
    /// for a floor normal (0, 1) this is +X.</summary>
    Right,
}

/// <summary>Math layer. The frame of a contact or a surface: a unit <see cref="Normal"/> and the
/// <see cref="Tangent"/> on the chosen side. Splits a vector into the part along the normal and the
/// part along the surface and builds one back, so contact and surface code reads as "speed into the
/// wall", "speed along the floor" instead of dot products.
/// <para>Deterministic: same inputs give the same bits on the same runtime. <see cref="Along"/>,
/// <see cref="Across"/> and <see cref="Into"/> are <see cref="Vector2.Dot"/> (as contact code written
/// with <c>Vector2</c> does); <see cref="Vector2.Dot"/> gives the same bits as
/// <c>v.X * n.X + v.Y * n.Y</c> except that a zero result is always +0, so they also replace that
/// scalar form wherever the result is compared, signed or passed through <c>Abs</c>. The TypeScript
/// twin (<c>@altruist/sim2d</c> <c>NormalFrame2D</c>) uses the scalar form.</para></summary>
public readonly struct NormalFrame2D
{
    /// <summary>The unit normal.</summary>
    public Vector2 Normal { get; }

    /// <summary>The unit tangent: <c>(-n.Y, n.X)</c> for <see cref="TangentSide2D.Left"/>,
    /// <c>(n.Y, -n.X)</c> for <see cref="TangentSide2D.Right"/>.</summary>
    public Vector2 Tangent { get; }

    /// <summary>A frame around the unit <paramref name="normal"/> with the tangent on
    /// <paramref name="side"/>.</summary>
    public NormalFrame2D(Vector2 normal, TangentSide2D side)
    {
        Normal = normal;
        Tangent = side == TangentSide2D.Left ? new Vector2(-normal.Y, normal.X) : new Vector2(normal.Y, -normal.X);
    }

    /// <summary>Tangent <c>(-n.Y, n.X)</c> (counter-clockwise of the normal).</summary>
    public static NormalFrame2D Left(Vector2 normal) => new(normal, TangentSide2D.Left);

    /// <summary>Tangent <c>(n.Y, -n.X)</c> (clockwise of the normal; +X on a floor).</summary>
    public static NormalFrame2D Right(Vector2 normal) => new(normal, TangentSide2D.Right);

    /// <summary>The component of <paramref name="v"/> along the normal (positive = away from the
    /// surface): <c>Vector2.Dot(v, Normal)</c>.</summary>
    public float Along(Vector2 v) => Vector2.Dot(v, Normal);

    /// <summary>The component of <paramref name="v"/> along the tangent (the motion along the
    /// surface): <c>Vector2.Dot(v, Tangent)</c>.</summary>
    public float Across(Vector2 v) => Vector2.Dot(v, Tangent);

    /// <summary>How fast <paramref name="v"/> goes into the surface (positive = toward it):
    /// <c>-Vector2.Dot(v, Normal)</c>.</summary>
    public float Into(Vector2 v) => -Vector2.Dot(v, Normal);

    /// <summary>The vector with <paramref name="alongNormal"/> along the normal and
    /// <paramref name="alongTangent"/> along the tangent:
    /// <c>Normal * alongNormal + Tangent * alongTangent</c>.</summary>
    public Vector2 Compose(float alongNormal, float alongTangent) => Normal * alongNormal + Tangent * alongTangent;

    /// <summary>The tangent turned to the side <paramref name="direction"/> points to (the way along
    /// the surface something is heading), or to <paramref name="fallbackSign"/> (±1) when
    /// <paramref name="direction"/> is square to the surface:
    /// <c>side = Scalar.SignOr(Across(direction), fallbackSign); (Tangent.X * side, Tangent.Y * side)</c>.
    /// Same bits as <c>side = MathF.Sign(d.X * t.X + d.Y * t.Y); if (side == 0) side = fallback</c>
    /// (a NaN direction gives the fallback instead of throwing).</summary>
    public Vector2 TangentToward(Vector2 direction, int fallbackSign)
    {
        var side = Scalar.SignOr(Across(direction), fallbackSign);
        return new Vector2(Tangent.X * side, Tangent.Y * side);
    }

    /// <summary>How far <paramref name="point"/> is from the surface through
    /// <paramref name="surfacePoint"/>, along the normal (negative = behind it):
    /// <c>(point.X - surfacePoint.X) * Normal.X + (point.Y - surfacePoint.Y) * Normal.Y</c>.</summary>
    public float HeightOf(Vector2 point, Vector2 surfacePoint) =>
        (point.X - surfacePoint.X) * Normal.X + (point.Y - surfacePoint.Y) * Normal.Y;
}
