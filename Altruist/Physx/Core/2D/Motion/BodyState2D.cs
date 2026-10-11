/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD;

/// <summary>Physics layer. A snapshot of a body's motion state (transform, velocities, awake,
/// enabled) to restore later or copy to another body: rollback, rewinding a scratch world for
/// prediction, resetting a body before a what-if rollout. Values are copied exactly.
/// <para>Not captured: forces / impulses pending in the engine, contacts, sleep timers, fixtures,
/// mass. A restored body therefore continues bit-identically only in an engine whose remaining
/// state also matches (e.g. a scratch world rebuilt the same way).</para>
/// <example><code>
/// var saved = body.CaptureState();          // or BodyState2D.Capture(body)
/// // ... step a what-if rollout ...
/// saved.Restore(body);                       // wakes the body by default
/// </code></example></summary>
/// <param name="Position">Body origin in world units (<see cref="IPhysxBody2D.Position"/>).</param>
/// <param name="Angle">Rotation in radians, counter-clockwise (<see cref="IPhysxBody2D.RotationZ"/>).</param>
/// <param name="LinearVelocity">Linear velocity in units/s.</param>
/// <param name="AngularVelocity">Angular velocity in rad/s, counter-clockwise (<see cref="IPhysxBody2D.AngularVelocityZ"/>).</param>
/// <param name="IsAwake">Whether the body was awake.</param>
/// <param name="IsEnabled">Whether the body took part in the simulation.</param>
public readonly record struct BodyState2D(
    Vector2 Position, float Angle, Vector2 LinearVelocity, float AngularVelocity, bool IsAwake = true, bool IsEnabled = true)
{
    /// <summary>Reads <paramref name="body"/>'s position, rotation, velocities, awake and enabled flags.
    /// Same as <see cref="BodyMotionExtensions2D.CaptureState"/>.</summary>
    public static BodyState2D Capture(IPhysxBody2D body) =>
        new(body.Position, body.RotationZ, body.LinearVelocity, body.AngularVelocityZ, body.IsAwake, body.IsEnabled);

    /// <summary>Writes the state to <paramref name="body"/>, in this order: <c>IsEnabled</c> (only
    /// with <paramref name="restoreEnabled"/>; first, so an engine re-creates its broad-phase
    /// entries where they were captured), <c>SetTransform(Position, Angle)</c>,
    /// <c>LinearVelocity</c>, <c>AngularVelocityZ</c>, then <c>IsAwake</c> (true when
    /// <paramref name="wake"/>, else the captured value).
    /// <para>To copy another live body's motion without a snapshot use
    /// <see cref="BodyMotionExtensions2D.CopyMotionFrom"/>.</para></summary>
    /// <param name="body">The body to write.</param>
    /// <param name="wake">True (default) forces the body awake; false restores the captured flag.</param>
    /// <param name="restoreEnabled">Also restore <see cref="IPhysxBody2D.IsEnabled"/> (default false).
    /// On Box2D, toggling it destroys or re-creates the body's contacts.</param>
    public void Restore(IPhysxBody2D body, bool wake = true, bool restoreEnabled = false)
    {
        if (restoreEnabled) body.IsEnabled = IsEnabled;
        body.SetTransform(Position, Angle);
        body.LinearVelocity = LinearVelocity;
        body.AngularVelocityZ = AngularVelocity;
        body.IsAwake = wake || IsAwake;
    }
}
