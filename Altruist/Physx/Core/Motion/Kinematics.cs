/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Physx;

/// <summary>Physics layer. One-dimensional motion under constant acceleration: how long, how far.
/// Dimension-agnostic (a speed along any axis).
/// <para>Deterministic: same inputs give the same bits on the same runtime; each helper evaluates
/// exactly the expressions in its summary.</para></summary>
/// <remarks>Scalar planning math only: pure functions on numbers, no body is read or changed. Units are whatever
/// you pass, as long as they are consistent (e.g. units, units/s, units/s², seconds). Alternatives:
/// <see cref="Altruist.Physx.TwoD.Ballistics2D"/> for 2D vector trajectories under gravity
/// (<see cref="Altruist.Physx.TwoD.Ballistics2D.ApexHeight"/> is the same formula as <see cref="StoppingDistance"/>
/// but returns 0 for non-upward speeds); <see cref="Altruist.Physx.TwoD.BodyMotionExtensions2D"/> (2D) or
/// <see cref="Altruist.Physx.ThreeD.BodySteeringExtensions3D"/> (3D) to actually change a body's velocity.</remarks>
/// <example><code>
/// float brakeAt = Kinematics.StoppingDistance(speed: 12f, deceleration: 30f);   // 2.4 units
/// float eta     = Kinematics.TimeToCover(distance: 20f, initialSpeed: 0f, acceleration: 10f, maxSpeed: 8f);
/// </code></example>
public static class Kinematics
{
    /// <summary>Distance to stop from <paramref name="speed"/> at a constant
    /// <paramref name="deceleration"/> (&gt; 0): <c>speed * speed / (2 * deceleration)</c>.
    /// With speed = vertical launch speed and deceleration = gravity it is the apex height.</summary>
    public static float StoppingDistance(float speed, float deceleration) => speed * speed / (2 * deceleration);

    /// <summary>Time to cover <paramref name="distance"/> (≥ 0) starting at
    /// <paramref name="initialSpeed"/> (≥ 0), accelerating at <paramref name="acceleration"/> (&gt; 0)
    /// up to <paramref name="maxSpeed"/> (&gt; 0), then cruising:
    /// <code>
    /// tAcc = MathF.Max(0, (maxSpeed - initialSpeed) / acceleration)
    /// dAcc = (initialSpeed + maxSpeed) / 2 * tAcc
    /// dAcc &gt;= distance ? (-initialSpeed + MathF.Sqrt(initialSpeed * initialSpeed + 2 * acceleration * distance)) / acceleration
    ///                   : tAcc + (distance - dAcc) / maxSpeed
    /// </code></summary>
    public static float TimeToCover(float distance, float initialSpeed, float acceleration, float maxSpeed)
    {
        var v0 = initialSpeed;
        var tAcc = MathF.Max(0, (maxSpeed - v0) / acceleration);
        var dAcc = (v0 + maxSpeed) / 2 * tAcc;
        if (dAcc >= distance) return (-v0 + MathF.Sqrt(v0 * v0 + 2 * acceleration * distance)) / acceleration;
        return tAcc + (distance - dAcc) / maxSpeed;
    }

    /// <summary>The centripetal acceleration of moving at <paramref name="speed"/> on a curve of
    /// <paramref name="radius"/>: <c>speed * speed / radius</c>. From a 2D velocity use
    /// <c>Velocity2D.CentripetalAcceleration</c>, which squares the components (no square root).</summary>
    public static float CentripetalAcceleration(float speed, float radius) => speed * speed / radius;
}
