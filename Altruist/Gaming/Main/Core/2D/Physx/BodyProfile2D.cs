/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>A reusable recipe for the physics body of a 2D world object: which body type and mass
    /// to create and which colliders to attach. Implement one per kind of actor (player, projectile,
    /// crate) so spawn code does not hand-build bodies.
    /// <para>Use it when the same body shape is created in several places; for a one-off body build
    /// <see cref="PhysxCollider2DParams"/> directly through the physics API. The 3D counterpart is
    /// <see cref="Altruist.Gaming.ThreeD.IBodyProfile3D"/>.</para></summary>
    public interface IBodyProfile2D
    {
        /// <summary>The body type and mass to create for an object at <paramref name="transform"/>.</summary>
        /// <param name="transform">Spawn transform of the object (position, rotation, size).</param>
        /// <returns>The body type (static / kinematic / dynamic) and its mass.</returns>
        (PhysxBodyType type, float mass) CreateBody(Transform2D transform);
        /// <summary>The colliders to attach to the body. Each collider's transform is its offset in
        /// the body; size conventions follow <see cref="PhysxCollider2DParams"/>.</summary>
        /// <param name="transform">Spawn transform of the object; implementations typically reuse it
        /// with a replaced size.</param>
        IEnumerable<PhysxCollider2DParams> CreateColliders(Transform2D transform);
    }

    /// <summary>A body profile for a character: one dynamic body with a single non-trigger
    /// <see cref="PhysxColliderShape2D.Capsule2D"/> collider (radius = <see cref="Radius"/>, half length =
    /// <see cref="HalfLength"/>, i.e. <c>Size = (Radius, HalfLength)</c>).
    /// <para>Note: the Box2D provider currently approximates a capsule as an oriented box of half
    /// extents (HalfLength, Radius), and <see cref="CreateBody"/> always returns
    /// <see cref="PhysxBodyType.Dynamic"/> (<see cref="IsKinematic"/> is stored but not applied).</para></summary>
    public sealed class HumanoidCapsuleBodyProfile2D : IBodyProfile2D
    {
        /// <summary>Capsule radius in world units.</summary>
        public float Radius { get; }
        /// <summary>Half the length of the capsule's straight segment, in world units.</summary>
        public float HalfLength { get; }
        /// <summary>Body mass returned by <see cref="CreateBody"/>.</summary>
        public float Mass { get; }
        /// <summary>Requested kinematic flag. Currently informational only: <see cref="CreateBody"/>
        /// ignores it and always returns a dynamic body.</summary>
        public bool IsKinematic { get; }

        /// <summary>Creates the profile.</summary>
        /// <param name="radius">Capsule radius in world units.</param>
        /// <param name="halfLength">Half length of the capsule's straight segment.</param>
        /// <param name="mass">Body mass.</param>
        /// <param name="isKinematic">Stored in <see cref="IsKinematic"/>; not applied by <see cref="CreateBody"/>.</param>
        public HumanoidCapsuleBodyProfile2D(
            float radius,
            float halfLength,
            float mass,
            bool isKinematic = false)
        {
            Radius = radius;
            HalfLength = halfLength;
            Mass = mass;
            IsKinematic = isKinematic;
        }

        /// <summary>Always <c>(PhysxBodyType.Dynamic, Mass)</c>; <paramref name="transform"/> is not used.</summary>
        /// <param name="transform">Ignored.</param>
        public (PhysxBodyType type, float mass) CreateBody(Transform2D transform)
        {
            return (PhysxBodyType.Dynamic, Mass);
        }

        /// <summary>Yields one solid capsule collider: <paramref name="transform"/> with its size
        /// replaced by <c>(Radius, HalfLength)</c>.</summary>
        /// <param name="transform">Collider offset in the body (position / rotation kept, size replaced).</param>
        public IEnumerable<PhysxCollider2DParams> CreateColliders(Transform2D transform)
        {
            var sized = transform.WithSize(Size2D.Of(Radius, HalfLength));
            yield return new PhysxCollider2DParams(PhysxColliderShape2D.Capsule2D, sized, isTrigger: false);
        }
    }
}
