/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. Velocity operations for hand-written 2D motion and contact response: impact speed,
/// cancelling motion into a surface, bounces, drag, scaled gravity, rigid-body point velocity.
/// Pure functions on <see cref="Vector2"/>; the body extensions in <c>Altruist.Physx.TwoD</c>
/// (<see cref="BodyMotionExtensions2D"/>) apply them to a body's velocity.
/// <para>Convention for gravity: side view, +Y up, <c>gravity</c> is the magnitude (≥ 0) of the
/// pull toward -Y.</para>
/// <para>Deterministic: same inputs give the same bits on the same runtime. Each helper evaluates
/// exactly the expression in its summary, so it can replace that expression written inline. The
/// TypeScript package <c>@altruist/sim2d</c> has the same functions.</para></summary>
public static class Velocity2D
{
    /// <summary>How fast <paramref name="velocity"/> closes in against the unit
    /// <paramref name="normal"/> (positive = moving into a surface whose normal points at the
    /// mover): <c>-Vector2.Dot(velocity, normal)</c>.</summary>
    public static float ApproachSpeed(Vector2 velocity, Vector2 normal) => -Vector2.Dot(velocity, normal);

    /// <summary>Removes <paramref name="amount"/> (1 = all) of the component of
    /// <paramref name="velocity"/> that goes against the unit <paramref name="normal"/>; a velocity
    /// moving away from it (or along it) is returned unchanged:
    /// <c>d = v.X * n.X + v.Y * n.Y; d &lt; 0 ? (v.X - n.X * d * amount, v.Y - n.Y * d * amount) : v</c>.
    /// Typical use: jump off a surface (cancel the fall into it, then <see cref="VectorMath2D.AddAlong"/>
    /// the jump speed), or a thrust that first cancels opposing motion.</summary>
    public static Vector2 CancelInto(Vector2 velocity, Vector2 normal, float amount = 1f)
    {
        var d = velocity.X * normal.X + velocity.Y * normal.Y;
        if (d < 0) return new Vector2(velocity.X - normal.X * d * amount, velocity.Y - normal.Y * d * amount);
        return velocity;
    }

    /// <summary>The velocity leaving a surface with unit <paramref name="normal"/>:
    /// <paramref name="normalSpeed"/> straight out along the normal plus
    /// <paramref name="tangentKeep"/> of the tangential part of <paramref name="velocity"/>:
    /// <c>along = Vector2.Dot(v, n); tangent = (v - n * along) * tangentKeep; return n * normalSpeed + tangent</c>.
    /// A restitution bounce is <c>Bounce(v, n, ApproachSpeed(v, n) * restitution, friction)</c>; a
    /// launch pad sets a fixed <paramref name="normalSpeed"/>.</summary>
    public static Vector2 Bounce(Vector2 velocity, Vector2 normal, float normalSpeed, float tangentKeep)
    {
        var along = Vector2.Dot(velocity, normal);
        var tangent = (velocity - normal * along) * tangentKeep;
        return normal * normalSpeed + tangent;
    }

    /// <summary>The velocity of a point of a rigid body: linear velocity plus ω × r, with
    /// <paramref name="offset"/> = point − center of mass:
    /// <c>linear + (-angular * offset.Y, angular * offset.X)</c>.</summary>
    public static Vector2 PointVelocity(Vector2 linear, float angular, Vector2 offset) =>
        linear + new Vector2(-angular * offset.Y, angular * offset.X);

    /// <summary>A constant acceleration along a direction for one step:
    /// <c>(v.X + direction.X * acceleration * dt, v.Y + direction.Y * acceleration * dt)</c>.
    /// Pass a negative <paramref name="acceleration"/> to push against the direction.</summary>
    public static Vector2 AccelerateAlong(Vector2 velocity, Vector2 direction, float acceleration, float dt) =>
        new(velocity.X + direction.X * acceleration * dt, velocity.Y + direction.Y * acceleration * dt);

    /// <summary>Linear drag for one step: <c>f = 1 - drag * dt; (v.X * f, v.Y * f)</c>.</summary>
    public static Vector2 ApplyLinearDrag(Vector2 velocity, float drag, float dt)
    {
        var f = 1 - drag * dt;
        return new Vector2(velocity.X * f, velocity.Y * f);
    }

    /// <summary>Gravity for one step, scaled per body: <c>(v.X, v.Y - gravity * dt * scale)</c>
    /// (<paramref name="gravity"/> pulls toward -Y). For bodies whose gravity is applied by hand
    /// (world gravity zero) so each body can scale it at will.</summary>
    public static Vector2 ApplyGravity(Vector2 velocity, float gravity, float dt, float scale = 1f) =>
        new(velocity.X, velocity.Y - gravity * dt * scale);

    /// <summary>Keeps the horizontal speed at least <paramref name="minSpeed"/> (a body that must
    /// never come to rest): when <c>MathF.Abs(v.X) &lt; minSpeed</c>, X becomes
    /// <c>dir * minSpeed</c> with <c>dir = v.X != 0 ? MathF.Sign(v.X) : fallbackDirection</c>;
    /// otherwise (and for NaN) <paramref name="velocity"/> is returned unchanged.</summary>
    public static Vector2 KeepMinimumSpeedX(Vector2 velocity, float minSpeed, int fallbackDirection)
    {
        if (!(MathF.Abs(velocity.X) < minSpeed)) return velocity;
        var dir = velocity.X != 0 ? MathF.Sign(velocity.X) : fallbackDirection;
        return new Vector2(dir * minSpeed, velocity.Y);
    }
}
