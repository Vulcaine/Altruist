/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD;

/// <summary>Gameplay layer. How a round body (a ball, a puck) leaves when another body strikes it: a
/// designed arcade response instead of the solver's, configured once per body kind and applied with
/// <see cref="Strike"/> or <see cref="Launch"/> from a <see cref="ContactImpact2D"/> captured before
/// the solver. Game code only scales the <see cref="Rebound"/> (strike kinds, combos).
/// <para>The target leaves along the contact normal; along the surface it blends its own motion with
/// the striker's (<see cref="SurfaceCarry"/>) and spins up from the slip between them.</para>
/// <para>Deterministic: every member evaluates exactly the expression in its summary (the TypeScript
/// twin <c>StrikeResponse2D</c> in <c>@altruist/sim2d/gameplay</c> uses the same order).</para>
/// <example><code>
/// var hit = ContactImpact2D.Capture(point, normal, striker, ball);
/// response.Strike(ball, hit, response.Rebound(hit) * strikeMultiplier, radius);
/// </code></example></summary>
public readonly record struct StrikeResponse2D
{
    /// <summary>How much of the closing speed bounces back on top of it (0 = the target only takes
    /// the closing speed, 1 = twice that).</summary>
    public float Restitution { get; init; }

    /// <summary>Share of the target's own speed it keeps on top of the strike.</summary>
    public float SpeedCarry { get; init; }

    /// <summary>Share of the striker's motion along the contact surface the target takes (the rest
    /// is its own).</summary>
    public float SurfaceCarry { get; init; }

    /// <summary>Spin from the slip between the surfaces, per unit of <c>slip / radius</c>.</summary>
    public float SpinFactor { get; init; }

    /// <summary>The strike's base speed along the normal: <c>hit.ClosingSpeed * (1 + Restitution)</c>.</summary>
    public float Rebound(in ContactImpact2D hit) => hit.ClosingSpeed * (1 + Restitution);

    /// <summary>The target leaves along the normal at <paramref name="power"/> plus
    /// <see cref="SpeedCarry"/> of its own speed: <c>Launch(target, hit, power + hit.TargetSpeed * SpeedCarry, radius)</c>.</summary>
    public void Strike(IPhysxBody2D target, in ContactImpact2D hit, float power, float radius) =>
        Launch(target, hit, power + hit.TargetSpeed * SpeedCarry, radius);

    /// <summary>The target leaves along the normal at exactly <paramref name="alongNormal"/>:
    /// <c>target.SetVelocityInFrame(hit.Frame, alongNormal, hit.TargetTangential * (1 - SurfaceCarry) + hit.StrikerTangential * SurfaceCarry)</c>,
    /// then <c>target.AddSpinFromSlip(hit.Slip, radius, SpinFactor)</c>.</summary>
    public void Launch(IPhysxBody2D target, in ContactImpact2D hit, float alongNormal, float radius)
    {
        var alongTangent = hit.TargetTangential * (1 - SurfaceCarry) + hit.StrikerTangential * SurfaceCarry;
        target.SetVelocityInFrame(hit.Frame, alongNormal, alongTangent);
        target.AddSpinFromSlip(hit.Slip, radius, SpinFactor);
    }
}
