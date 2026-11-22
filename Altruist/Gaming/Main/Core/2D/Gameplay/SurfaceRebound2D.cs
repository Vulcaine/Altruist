/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD;

/// <summary>Gameplay layer. How a body rebounds when it is driven into a surface (a dash into a wall,
/// a bumper): configured once per body kind (e.g. loaded from the game's config) and applied with
/// <see cref="Apply"/>, so game code only decides <i>whether</i> it rebounds and by how much more.
/// <para>Deterministic: every member evaluates exactly the expression in its summary (the TypeScript
/// twin <c>SurfaceRebound2D</c> in <c>@altruist/sim2d/gameplay</c> uses the same order).</para>
/// <example><code>
/// var speed = Velocity2D.ApproachSpeed(incoming, normal);
/// if (rebound.IsImpact(speed) &amp;&amp; dashing)
///     rebound.Apply(body, normal, rebound.OutSpeed(speed) * bonus, incoming);
/// </code></example></summary>
public readonly record struct SurfaceRebound2D
{
    /// <summary>Slower approaches (along the normal) are touches, not impacts.</summary>
    public float MinImpactSpeed { get; init; }

    /// <summary>Share of the approach speed the body leaves with.</summary>
    public float Restitution { get; init; }

    /// <summary>The body always leaves at least this fast.</summary>
    public float MinOutSpeed { get; init; }

    /// <summary>Share of the motion along the surface the body keeps.</summary>
    public float TangentKeep { get; init; }

    /// <summary>Is an approach this fast an impact? <c>!(approachSpeed &lt; MinImpactSpeed)</c>.</summary>
    public bool IsImpact(float approachSpeed) => !(approachSpeed < MinImpactSpeed);

    /// <summary>The speed the body leaves with: <c>MathF.Max(approachSpeed * Restitution, MinOutSpeed)</c>.</summary>
    public float OutSpeed(float approachSpeed) => MathF.Max(approachSpeed * Restitution, MinOutSpeed);

    /// <summary>Rebounds the body from its recorded <paramref name="incoming"/> velocity:
    /// <paramref name="outSpeed"/> out along the unit <paramref name="normal"/>, keeping
    /// <see cref="TangentKeep"/> of the motion along the surface
    /// (<c>body.BounceOff(normal, outSpeed, TangentKeep, incoming)</c>).</summary>
    public void Apply(IPhysxBody2D body, Vector2 normal, float outSpeed, Vector2 incoming) =>
        body.BounceOff(normal, outSpeed, TangentKeep, incoming);
}
