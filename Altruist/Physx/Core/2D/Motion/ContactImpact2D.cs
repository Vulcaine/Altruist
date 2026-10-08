/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. One body (the striker: a bat, a kicker, a vehicle) hitting another (the
/// target: a ball, a puck) at a contact point, captured before the solver changes the velocities:
/// how fast they close in, how hard the striker itself moves into the target, and the motion along
/// the contact surface on both sides (slip, carry, spin). Build the response with
/// <see cref="Frame"/> (<c>target.SetVelocityInFrame(impact.Frame, outN, outT)</c>,
/// <c>target.AddSpinFromSlip(impact.Slip, radius, k)</c>).
/// <para><see cref="Normal"/> points from the striker into the target. The frame's tangent is
/// <see cref="TangentSide2D.Left"/>: <c>(-n.Y, n.X)</c>.</para>
/// <para>Deterministic: every member evaluates exactly the expression in its summary (the
/// <c>Vector2</c> forms of hand-written contact code), so it replaces that code without changing a
/// bit. The TypeScript twin uses the scalar forms (<c>a.x * b.x + a.y * b.y</c>) and measures
/// <see cref="TargetSpeed"/> with <c>Math.sqrt(x * x + y * y)</c>.</para></summary>
public readonly struct ContactImpact2D
{
    /// <summary>The contact point (world).</summary>
    public Vector2 Point { get; }

    /// <summary>The unit contact normal, striker → target.</summary>
    public Vector2 Normal { get; }

    /// <summary>The striker's linear velocity (at its center of mass).</summary>
    public Vector2 StrikerVelocity { get; }

    /// <summary>The striker's angular velocity (rad/s, counter-clockwise).</summary>
    public float StrikerSpin { get; }

    /// <summary>The striker's center of mass (world).</summary>
    public Vector2 StrikerCenter { get; }

    /// <summary>The target's linear velocity.</summary>
    public Vector2 TargetVelocity { get; }

    /// <summary>The striker's velocity at the contact point, spin included:
    /// <c>Velocity2D.PointVelocity(StrikerVelocity, StrikerSpin, Point - StrikerCenter)</c>.</summary>
    public Vector2 StrikerPointVelocity { get; }

    public ContactImpact2D(Vector2 point, Vector2 normal, Vector2 strikerVelocity, float strikerSpin, Vector2 strikerCenter,
                           Vector2 targetVelocity)
    {
        Point = point;
        Normal = normal;
        StrikerVelocity = strikerVelocity;
        StrikerSpin = strikerSpin;
        StrikerCenter = strikerCenter;
        TargetVelocity = targetVelocity;
        StrikerPointVelocity = Velocity2D.PointVelocity(strikerVelocity, strikerSpin, point - strikerCenter);
    }

    /// <summary>Captures the bodies' motion at a contact (call it in pre-solve):
    /// <c>new(point, normal, striker.LinearVelocity, striker.AngularVelocityZ, striker.WorldCenter, target.LinearVelocity)</c>.</summary>
    public static ContactImpact2D Capture(Vector2 point, Vector2 normal, IPhysxBody2D striker, IPhysxBody2D target) =>
        new(point, normal, striker.LinearVelocity, striker.AngularVelocityZ, striker.WorldCenter, target.LinearVelocity);

    /// <summary>The contact frame: <c>NormalFrame2D.Left(Normal)</c> (tangent <c>(-n.Y, n.X)</c>).</summary>
    public NormalFrame2D Frame => NormalFrame2D.Left(Normal);

    /// <summary>How fast the contact point of the striker closes in on the target along the normal
    /// (positive = they are coming together): <c>Vector2.Dot(StrikerPointVelocity - TargetVelocity, Normal)</c>.
    /// The difference first, as written; not <c>Dot(cp, n) - Dot(vb, n)</c>.</summary>
    public float ClosingSpeed => Vector2.Dot(StrikerPointVelocity - TargetVelocity, Normal);

    /// <summary>How fast the striker itself moves into the target (positive = into it; a target
    /// landing on a still striker closes in but is not struck): <c>Vector2.Dot(StrikerPointVelocity, Normal)</c>.</summary>
    public float StrikerInto => Vector2.Dot(StrikerPointVelocity, Normal);

    /// <summary>The target's speed: <c>TargetVelocity.Length()</c> (the bits of
    /// <c>MathF.Sqrt(x * x + y * y)</c>).</summary>
    public float TargetSpeed => TargetVelocity.Length();

    /// <summary>The target's motion along the contact surface: <c>Frame.Across(TargetVelocity)</c>
    /// (<c>Vector2.Dot(TargetVelocity, (-n.Y, n.X))</c>).</summary>
    public float TargetTangential => Frame.Across(TargetVelocity);

    /// <summary>The striker's contact-point motion along the surface: <c>Frame.Across(StrikerPointVelocity)</c>.</summary>
    public float StrikerTangential => Frame.Across(StrikerPointVelocity);

    /// <summary>How fast the striker's surface slides past the target's:
    /// <c>StrikerTangential - TargetTangential</c> (spin it gives: <c>AddSpinFromSlip</c>).</summary>
    public float Slip => StrikerTangential - TargetTangential;
}
