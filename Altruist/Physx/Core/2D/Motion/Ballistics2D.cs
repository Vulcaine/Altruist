/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. Free flight under constant gravity, in closed form and stepped:
/// where a projectile will be, how high it rises, how to throw it to a target, and a cheap
/// fixed-step predictor (with an optional bouncing floor) for "where will it go" queries.
/// <para>Convention: side view, +Y up, <c>gravity</c> is the magnitude (≥ 0) of the pull
/// toward -Y. Same convention as <see cref="Velocity2D.ApplyGravity"/>.</para>
/// <para>Deterministic: same inputs give the same bits on the same runtime. Each helper evaluates
/// exactly the expressions in its summary. The stepped predictor is semi-implicit Euler (velocity
/// first, then position): it is NOT bit-identical to a physics engine's integration, use the
/// engine itself (a scratch world) when you need that.</para></summary>
public static class Ballistics2D
{
    /// <summary>Closed-form position after <paramref name="t"/> seconds:
    /// <c>(p.X + v.X * t, p.Y + v.Y * t - 0.5f * gravity * t * t)</c>.</summary>
    public static Vector2 PositionAt(Vector2 position, Vector2 velocity, float gravity, float t) =>
        new(position.X + velocity.X * t, position.Y + velocity.Y * t - 0.5f * gravity * t * t);

    /// <summary>Closed-form velocity after <paramref name="t"/> seconds: <c>(v.X, v.Y - gravity * t)</c>.</summary>
    public static Vector2 VelocityAt(Vector2 velocity, float gravity, float t) => new(velocity.X, velocity.Y - gravity * t);

    /// <summary>How much higher a body launched upward at <paramref name="verticalSpeed"/> still
    /// rises (0 when not moving up): <c>vy &gt; 0 ? vy * vy / (2 * gravity) : 0</c>.</summary>
    public static float ApexHeight(float verticalSpeed, float gravity) =>
        verticalSpeed > 0 ? verticalSpeed * verticalSpeed / (2 * gravity) : 0;

    /// <summary>The vertical launch speed that rises by <paramref name="rise"/> (negative = drops)
    /// in exactly <paramref name="t"/> seconds: <c>(rise + 0.5f * gravity * t * t) / t</c>.</summary>
    public static float LaunchSpeedY(float rise, float gravity, float t) => (rise + 0.5f * gravity * t * t) / t;

    /// <summary>The launch velocity that covers <paramref name="delta"/> (target − start) in exactly
    /// <paramref name="t"/> seconds: <c>(delta.X / t, (delta.Y + 0.5f * gravity * t * t) / t)</c>.</summary>
    public static Vector2 LaunchVelocity(Vector2 delta, float gravity, float t) =>
        new(delta.X / t, LaunchSpeedY(delta.Y, gravity, t));

    /// <summary>One semi-implicit Euler step:
    /// <c>v.Y -= gravity * dt; p.X += v.X * dt; p.Y += v.Y * dt</c>.</summary>
    public static void Step(ref Vector2 position, ref Vector2 velocity, float gravity, float dt)
    {
        velocity.Y -= gravity * dt;
        position.X += velocity.X * dt;
        position.Y += velocity.Y * dt;
    }

    /// <summary>A floor at height <paramref name="floorY"/> (e.g. ground + radius): when below it and
    /// falling, the position is put back on it and the vertical speed reflected with
    /// <paramref name="restitution"/>: <c>if (p.Y &lt; floorY &amp;&amp; v.Y &lt; 0) { p.Y = floorY; v.Y = -v.Y * restitution; }</c>.
    /// Returns whether it bounced.</summary>
    public static bool BounceOnFloor(ref Vector2 position, ref Vector2 velocity, float floorY, float restitution)
    {
        if (!(position.Y < floorY && velocity.Y < 0)) return false;
        position.Y = floorY;
        velocity.Y = -velocity.Y * restitution;
        return true;
    }

    /// <summary>State after <paramref name="steps"/> calls of <see cref="Step"/>.</summary>
    public static (Vector2 Position, Vector2 Velocity) Predict(Vector2 position, Vector2 velocity, float gravity, float dt, int steps)
    {
        for (var i = 0; i < steps; i++) Step(ref position, ref velocity, gravity, dt);
        return (position, velocity);
    }

    /// <summary>State after <paramref name="steps"/> calls of <see cref="Step"/>, each followed by
    /// <see cref="BounceOnFloor"/>.</summary>
    public static (Vector2 Position, Vector2 Velocity) Predict(Vector2 position, Vector2 velocity, float gravity, float dt, int steps,
                                                                float floorY, float restitution)
    {
        for (var i = 0; i < steps; i++)
        {
            Step(ref position, ref velocity, gravity, dt);
            BounceOnFloor(ref position, ref velocity, floorY, restitution);
        }
        return (position, velocity);
    }

    /// <summary>Steps up to <paramref name="steps"/> times and returns the number of the first step
    /// (1-based) whose position satisfies <paramref name="predicate"/>, or -1 when none does.</summary>
    public static int FirstStepWhere(Vector2 position, Vector2 velocity, float gravity, float dt, int steps, Func<Vector2, bool> predicate)
    {
        for (var i = 0; i < steps; i++)
        {
            Step(ref position, ref velocity, gravity, dt);
            if (predicate(position)) return i + 1;
        }
        return -1;
    }
}
