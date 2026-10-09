/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD;

/// <summary>Gameplay layer. A box-shaped body that faces left or right (a side-view vehicle, a
/// fighter, a creature): its nose and roof directions, its nose tip, which side of it something
/// touched, whether a point is in front of it, the underside corners and the rotation that aims the
/// nose somewhere. Cheap to make (a view over the body, nothing cached): build one when needed, e.g.
/// <c>new FacingFrame2D(body, facing, halfWidth, halfHeight)</c>.
/// <para>Conventions: <see cref="Facing"/> +1 puts the nose on the body's local +X, -1 on local -X;
/// the roof is local +Y (Box2D). Sides are reported in facing terms (<see cref="BoxSide2D.Front"/> =
/// the nose side, <see cref="BoxSide2D.Top"/> = the roof).</para>
/// <para>Deterministic: each member evaluates exactly the expression in its summary, through the
/// body's own <c>GetWorldVector</c> / <c>GetLocalPoint</c> / <c>GetLocalVector</c> (an engine body's
/// native ones), so it gives the bits of the same code written inline.</para></summary>
public readonly struct FacingFrame2D
{
    /// <summary>Wraps <paramref name="body"/> as a box of the given half extents facing
    /// <paramref name="facing"/>. Allocation-free; nothing is read from the body until a member is used.</summary>
    /// <param name="body">The body to view (its live transform is read on every member access).</param>
    /// <param name="facing">+1 = nose on local +X, -1 = nose on local -X.</param>
    /// <param name="halfWidth">Half the box length along the nose axis (local X), in world units.</param>
    /// <param name="halfHeight">Half the box height along the roof axis (local Y), in world units.</param>
    public FacingFrame2D(IPhysxBody2D body, int facing, float halfWidth, float halfHeight)
    {
        Body = body;
        Facing = facing;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
    }

    /// <summary>The viewed body.</summary>
    public IPhysxBody2D Body { get; }

    /// <summary>+1: the nose is local +X; -1: local -X.</summary>
    public int Facing { get; }

    /// <summary>Half the box length along the nose axis (local X).</summary>
    public float HalfWidth { get; }
    /// <summary>Half the box height along the roof axis (local Y).</summary>
    public float HalfHeight { get; }

    /// <summary>The unit direction out of the nose: <c>Body.GetWorldVector((Facing, 0))</c>.</summary>
    public Vector2 Nose => Body.GetWorldVector(new Vector2(Facing, 0));

    /// <summary>The unit direction out of the roof: <c>Body.UpDirection()</c>
    /// (<c>GetWorldVector((0, 1))</c>).</summary>
    public Vector2 Up => Body.UpDirection();

    /// <summary>The middle of the nose end: <c>Body.Position + Nose * HalfWidth</c>.</summary>
    public Vector2 NoseTip => Body.Position + Nose * HalfWidth;

    /// <summary>Is the world point on the nose side of the body's center?
    /// <c>Body.GetLocalPoint(worldPoint).X * Facing &gt; 0</c>.</summary>
    public bool IsInFront(Vector2 worldPoint) => Body.GetLocalPoint(worldPoint).X * Facing > 0;

    /// <summary>How well the nose lines up with <paramref name="direction"/> (1 = straight along it for
    /// a unit direction, -1 = against it): <c>Nose.X * direction.X + Nose.Y * direction.Y</c>.</summary>
    public float NoseAlignment(Vector2 direction)
    {
        var nose = Nose;
        return nose.X * direction.X + nose.Y * direction.Y;
    }

    /// <summary>Which side of the body the world point is on (where a contact touched it):
    /// <c>local = Body.GetLocalPoint(worldPoint); Geometry2D.ClassifyBoxSide((local.X * Facing, local.Y), HalfWidth, HalfHeight, axisBias)</c>.
    /// <paramref name="axisBias"/> &gt; 1 favors front / back on the corners.</summary>
    public BoxSide2D ZoneAt(Vector2 worldPoint, float axisBias)
    {
        var local = Body.GetLocalPoint(worldPoint);
        return Geometry2D.ClassifyBoxSide(new Vector2(local.X * Facing, local.Y), HalfWidth, HalfHeight, axisBias);
    }

    /// <summary>Which side of the body faces the world direction (e.g. <c>-normal</c> of a surface it
    /// hit: the side that hit it):
    /// <c>local = Body.GetLocalVector(worldDirection); Geometry2D.ClassifyBoxSide((local.X * Facing, local.Y), HalfWidth, HalfHeight, axisBias)</c>.</summary>
    public BoxSide2D ZoneToward(Vector2 worldDirection, float axisBias)
    {
        var local = Body.GetLocalVector(worldDirection);
        return Geometry2D.ClassifyBoxSide(new Vector2(local.X * Facing, local.Y), HalfWidth, HalfHeight, axisBias);
    }

    /// <summary>The two bottom corners (behind the nose, then at the nose), e.g. to cast rays down from
    /// both ends: <c>Body.Position + Nose * (HalfWidth * end) - Up * HalfHeight</c> for <c>end</c> = -1, then +1.</summary>
    public (Vector2 Back, Vector2 Front) UndersideEnds()
    {
        var up = Up;
        var nose = Nose;
        var c = Body.Position;
        var back = c + nose * (HalfWidth * -1) - up * HalfHeight;
        var front = c + nose * (HalfWidth * 1) - up * HalfHeight;
        return (back, front);
    }

    /// <summary>The body rotation that points the nose along <paramref name="direction"/> (aim with a
    /// stick, align with the velocity): <c>Rotation2D.AngleAligningForward(direction, Facing)</c>, i.e.
    /// <c>MathF.Atan2(d.Y, d.X) - (Facing &gt; 0 ? 0 : π)</c>.</summary>
    public float AimRotationToward(Vector2 direction) => Rotation2D.AngleAligningForward(direction, Facing);
}
