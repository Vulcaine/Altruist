/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>Math layer. Vector algebra on <see cref="Vector2"/> that <see cref="Vector2"/> itself does not
/// offer: safe normalize, 2D cross products, projections, components along an axis.
/// <para>Deterministic: same inputs give the same bits on the same runtime. Each helper evaluates
/// exactly the expression in its summary (component by component, no fused multiply-add), so it
/// can replace that expression written inline. The TypeScript package <c>@altruist/sim2d</c>
/// has the same functions on <c>{ x, y }</c> objects.</para></summary>
public static class VectorMath2D
{
    /// <summary><c>MathF.Sqrt(x * x + y * y)</c>.</summary>
    public static float Length(float x, float y) => MathF.Sqrt(x * x + y * y);

    /// <summary><c>MathF.Sqrt(v.X * v.X + v.Y * v.Y)</c> (the same bits as
    /// <see cref="Vector2.Length"/>).</summary>
    public static float Length(Vector2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>Unit vector along <paramref name="v"/>, or zero for a zero vector:
    /// <c>l = Length(v); if (l == 0) l = 1; return (v.X / l, v.Y / l)</c>. No epsilon: any
    /// non-zero vector is normalized (compare <see cref="Direction2D.Between(Vector2,Vector2)"/>,
    /// which treats vectors shorter than 1e-6 as zero).</summary>
    public static Vector2 NormalizeOrZero(Vector2 v)
    {
        var l = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (l == 0) l = 1;
        return new Vector2(v.X / l, v.Y / l);
    }

    /// <summary><see cref="NormalizeOrZero(Vector2)"/> that also returns the length (0 for a zero
    /// vector).</summary>
    public static Vector2 NormalizeOrZero(Vector2 v, out float length)
    {
        var l = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        length = l;
        if (l == 0) l = 1;
        return new Vector2(v.X / l, v.Y / l);
    }

    /// <summary>Unit vector along <paramref name="v"/>, or <paramref name="fallback"/> (returned as
    /// given) for a zero vector: <c>l = Length(v); l == 0 ? fallback : (v.X / l, v.Y / l)</c>.</summary>
    public static Vector2 NormalizeOr(Vector2 v, Vector2 fallback)
    {
        var l = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return l == 0 ? fallback : new Vector2(v.X / l, v.Y / l);
    }

    /// <summary>The 2D cross product (z of the 3D cross): <c>a.X * b.Y - a.Y * b.X</c>. Positive
    /// when <paramref name="b"/> is counter-clockwise of <paramref name="a"/>.</summary>
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Angular velocity × offset (ω ẑ × r): <c>(-w * r.Y, w * r.X)</c>.</summary>
    public static Vector2 Cross(float w, Vector2 r) => new(-w * r.Y, w * r.X);

    /// <summary>The part of <paramref name="v"/> along the unit <paramref name="axis"/>:
    /// <c>axis * Vector2.Dot(v, axis)</c>.</summary>
    public static Vector2 Project(Vector2 v, Vector2 axis) => axis * Vector2.Dot(v, axis);

    /// <summary>The part of <paramref name="v"/> across the unit <paramref name="normal"/> (the
    /// tangential part): <c>v - normal * Vector2.Dot(v, normal)</c>.</summary>
    public static Vector2 Reject(Vector2 v, Vector2 normal) => v - normal * Vector2.Dot(v, normal);

    /// <summary><paramref name="v"/> plus <paramref name="amount"/> along
    /// <paramref name="direction"/>: <c>(v.X + direction.X * amount, v.Y + direction.Y * amount)</c>.</summary>
    public static Vector2 AddAlong(Vector2 v, Vector2 direction, float amount) =>
        new(v.X + direction.X * amount, v.Y + direction.Y * amount);

    /// <summary>Sets the component of <paramref name="v"/> along the unit
    /// <paramref name="direction"/> to <paramref name="value"/>, keeping the rest:
    /// <c>c = v.X * direction.X + v.Y * direction.Y; AddAlong(v, direction, value - c)</c>.</summary>
    public static Vector2 WithComponentAlong(Vector2 v, Vector2 direction, float value)
    {
        var c = v.X * direction.X + v.Y * direction.Y;
        return AddAlong(v, direction, value - c);
    }

    /// <summary>Moves the component of <paramref name="v"/> along the unit
    /// <paramref name="direction"/> toward <paramref name="target"/> by at most
    /// <paramref name="maxDelta"/>, keeping the rest:
    /// <c>c = v·direction; AddAlong(v, direction, Scalar.Approach(c, target, maxDelta) - c)</c>.</summary>
    public static Vector2 ApproachComponentAlong(Vector2 v, Vector2 direction, float target, float maxDelta)
    {
        var c = v.X * direction.X + v.Y * direction.Y;
        var next = Scalar.Approach(c, target, maxDelta);
        return AddAlong(v, direction, next - c);
    }

    /// <summary>Turns all of <paramref name="v"/>'s length onto the unit <paramref name="axis"/>,
    /// on the side <paramref name="v"/> points to (+axis when perpendicular):
    /// <c>speed = Length(v); s = v·axis &gt;= 0 ? speed : -speed; return axis * s</c>.</summary>
    public static Vector2 RedirectAlong(Vector2 v, Vector2 axis)
    {
        var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        var s = v.X * axis.X + v.Y * axis.Y >= 0 ? speed : -speed;
        return new Vector2(axis.X * s, axis.Y * s);
    }

    /// <summary><paramref name="v"/> shortened to <paramref name="maxLength"/> when longer:
    /// <c>s = v.Length(); s &gt; maxLength ? v / s * maxLength : v</c>.</summary>
    public static Vector2 ClampLength(Vector2 v, float maxLength)
    {
        var s = v.Length();
        return s > maxLength ? v / s * maxLength : v;
    }

    /// <summary><paramref name="v"/> shortened by <paramref name="amount"/>, given its
    /// <paramref name="length"/> (&gt; 0, already computed by the caller):
    /// <c>(v.X - v.X / length * amount, v.Y - v.Y / length * amount)</c>.</summary>
    public static Vector2 ShortenBy(Vector2 v, float length, float amount) =>
        new(v.X - v.X / length * amount, v.Y - v.Y / length * amount);

    /// <summary><paramref name="v"/> rotated counter-clockwise by <paramref name="degrees"/>:
    /// <c>r = degrees * MathF.PI / 180; c = MathF.Cos(r); s = MathF.Sin(r); (v.X * c - v.Y * s, v.X * s + v.Y * c)</c>.
    /// The radians are <c>(degrees * π) / 180</c>, not <see cref="Angle.ToRadians"/>'s
    /// <c>degrees * (π / 180)</c> (the two can differ in the last bit). The TypeScript twin
    /// <c>rotateDegrees</c> takes its cosine and sine from <c>Direction2D.fromPolarDegrees</c>
    /// (<c>degrees * (π / 180)</c>).</summary>
    public static Vector2 RotateDegrees(Vector2 v, float degrees)
    {
        var c = MathF.Cos(degrees * MathF.PI / 180);
        var s = MathF.Sin(degrees * MathF.PI / 180);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>Linear interpolation <c>a + (b - a) * t</c> (unclamped). Not
    /// <see cref="Vector2.Lerp"/>, whose formula differs between .NET versions.</summary>
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;
}
