/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. A snapshot of a body's motion state (transform, velocities, awake,
/// enabled) to restore later or copy to another body: rollback, rewinding a scratch world for
/// prediction, resetting a body before a what-if rollout. Values are copied exactly.</summary>
public readonly record struct BodyState2D(
    Vector2 Position, float Angle, Vector2 LinearVelocity, float AngularVelocity, bool IsAwake = true, bool IsEnabled = true)
{
    public static BodyState2D Capture(IPhysxBody2D body) =>
        new(body.Position, body.RotationZ, body.LinearVelocity, body.AngularVelocityZ, body.IsAwake, body.IsEnabled);

    /// <summary>Writes the state to <paramref name="body"/>, in this order: <c>IsEnabled</c> (only
    /// with <paramref name="restoreEnabled"/>; first, so an engine re-creates its broad-phase
    /// entries where they were captured), <c>SetTransform(Position, Angle)</c>,
    /// <c>LinearVelocity</c>, <c>AngularVelocityZ</c>, then <c>IsAwake</c> (true when
    /// <paramref name="wake"/>, else the captured value).</summary>
    public void Restore(IPhysxBody2D body, bool wake = true, bool restoreEnabled = false)
    {
        if (restoreEnabled) body.IsEnabled = IsEnabled;
        body.SetTransform(Position, Angle);
        body.LinearVelocity = LinearVelocity;
        body.AngularVelocityZ = AngularVelocity;
        body.IsAwake = wake || IsAwake;
    }
}
