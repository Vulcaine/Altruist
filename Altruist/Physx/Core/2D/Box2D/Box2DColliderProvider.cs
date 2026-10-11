/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Physx.TwoD;

/// <summary>
/// The <see cref="IPhysxColliderApiProvider2D"/> DI service: creates detached <see cref="Collider2D"/>s
/// (the same as the static <see cref="PhysxCollider2D"/> shortcuts). The Box2D fixtures are created when
/// the collider is attached with <see cref="Box2DPhysxBodyApiProvider2D.AddCollider"/>.
/// </summary>
[Service(typeof(IPhysxColliderApiProvider2D))]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
public sealed class Box2DPhysxColliderApiProvider2D : IPhysxColliderApiProvider2D
{
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A polygon has fewer than 3 vertices, or the shape is a chain or an edge.</exception>
    public IPhysxCollider2D CreateCollider(in PhysxCollider2DParams p) => Collider2D.From(p);
}
