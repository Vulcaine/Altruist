/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.TwoD.Numerics;

/// <summary>"How far apart are two points?" — 2D mirror of
/// <see cref="Altruist.ThreeD.Numerics.Distance"/>. No <c>Horizontal</c>
/// variant: 2D has no vertical axis to exclude.</summary>
public static class Distance2D
{
    public static float Between(Vector2 a, Vector2 b) => Vector2.Distance(a, b);
    public static float Between(Position2D a, Position2D b) => Between(a.ToFloatVector2(), b.ToFloatVector2());

    public static float Squared(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b);
    public static float Squared(Position2D a, Position2D b) => Squared(a.ToFloatVector2(), b.ToFloatVector2());
}
