/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// Engine-agnostic descriptor for a 3D collider. Build it with the <see cref="PhysxCollider3D"/> factory and pass it to
    /// <see cref="IPhysxColliderApiProvider3D.CreateCollider"/>.
    /// </summary>
    public readonly struct PhysxCollider3DDesc
    {
        /// <summary>Logical id for this collider descriptor.</summary>
        public string Id { get; }

        /// <summary>Primitive shape.</summary>
        public PhysxColliderShape3D Shape { get; }
        /// <summary>Shape transform; <c>Size</c> encodes the dimensions (see <see cref="PhysxColliderShape3D"/>).</summary>
        public Transform3D Transform { get; }
        /// <summary>Whether the collider is a trigger (overlap-only).</summary>
        public bool IsTrigger { get; }

        /// <summary>Height samples for heightfield colliders; <see langword="null"/> otherwise.</summary>
        public HeightfieldData? Heightfield { get; }

        /// <summary>Creates a descriptor. Prefer the <see cref="PhysxCollider3D"/> factory, which generates the id and encodes the size.</summary>
        /// <param name="id">Unique collider id.</param>
        /// <param name="shape">Primitive shape.</param>
        /// <param name="transform">Shape transform with the size encoding.</param>
        /// <param name="heightmap">Height samples for <see cref="PhysxColliderShape3D.Heightfield3D"/>.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public PhysxCollider3DDesc(string id, PhysxColliderShape3D shape, Transform3D transform, HeightfieldData? heightmap = null, bool isTrigger = false)
        {
            Id = id;
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
            Heightfield = heightmap;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return
                $"PhysxCollider3DDesc(Id={Id}, Shape={Shape}, Transform={Transform}, IsTrigger={IsTrigger}, Heightfield={Heightfield})";
        }
    }

    /// <summary>
    /// Static helpers for building engine-agnostic collider descriptors (pure data). Each call generates a new GUID id.
    /// Prefer the shape-specific helpers (<see cref="CreateSphere"/>, <see cref="CreateBox"/>, <see cref="CreateCapsule"/>,
    /// <see cref="CreateHeightmap"/>), which encode the dimensions into <c>Transform.Size</c> correctly.
    /// </summary>
    /// <example>
    /// <code>
    /// var sphere  = colliderApi.CreateCollider(PhysxCollider3D.CreateSphere(0.5f));
    /// var capsule = colliderApi.CreateCollider(PhysxCollider3D.CreateCapsule(radius: 0.4f, halfLength: 0.6f));
    /// </code>
    /// </example>
    public static class PhysxCollider3D
    {
        /// <summary>Creates a descriptor from a shape and a transform whose <c>Size</c> already uses the shape's encoding (see <see cref="PhysxColliderShape3D"/>).</summary>
        /// <param name="shape">Primitive shape.</param>
        /// <param name="transform">Transform with encoded size.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public static PhysxCollider3DDesc Create(
            PhysxColliderShape3D shape,
            Transform3D transform,
            bool isTrigger = false)
        {
            var id = Guid.NewGuid().ToString("N");
            return new PhysxCollider3DDesc(id, shape, transform, isTrigger: isTrigger);
        }

        /// <summary>Creates a heightfield (terrain) collider descriptor.</summary>
        /// <param name="data">Height samples, e.g. from a heightmap loader.</param>
        /// <param name="transform">Collider transform.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public static PhysxCollider3DDesc CreateHeightmap(
        HeightfieldData data,
        Transform3D transform,
        bool isTrigger = false)
        {
            var id = Guid.NewGuid().ToString("N");
            return new PhysxCollider3DDesc(
                id,
                PhysxColliderShape3D.Heightfield3D,
                transform,
                isTrigger: isTrigger,
                heightmap: data);
        }

        /// <summary>Creates a sphere collider descriptor (stores the radius in <c>Size.X</c>).</summary>
        /// <param name="radius">Sphere radius in world units.</param>
        /// <param name="transform">Base transform; defaults to <see cref="Transform3D.Zero"/>. Its size is replaced.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public static PhysxCollider3DDesc CreateSphere(
            float radius,
            Transform3D? transform = null,
            bool isTrigger = false)
        {
            var baseTransform = transform ?? Transform3D.Zero;
            var sized = baseTransform.WithSize(Size3D.Of(radius, 0f, 0f));
            return Create(PhysxColliderShape3D.Sphere3D, sized, isTrigger);
        }

        /// <summary>Creates a box collider descriptor (stores the half extents in <c>Size</c>).</summary>
        /// <param name="halfExtents">Half the box size along X, Y and Z.</param>
        /// <param name="transform">Base transform; defaults to <see cref="Transform3D.Zero"/>. Its size is replaced.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public static PhysxCollider3DDesc CreateBox(
            Vector3 halfExtents,
            Transform3D? transform = null,
            bool isTrigger = false)
        {
            var baseTransform = transform ?? Transform3D.Zero;
            var sized = baseTransform.WithSize(Size3D.From(halfExtents));
            return Create(PhysxColliderShape3D.Box3D, sized, isTrigger);
        }

        /// <summary>
        /// Creates a Y-aligned capsule collider descriptor (stores <c>Size = (radius, halfLength, 0)</c>). Total height is
        /// <c>2 * (halfLength + radius)</c>.
        /// </summary>
        /// <param name="radius">Capsule radius.</param>
        /// <param name="halfLength">Half the length of the inner segment between the hemisphere centres.</param>
        /// <param name="transform">Base transform; defaults to <see cref="Transform3D.Zero"/>. Its size is replaced.</param>
        /// <param name="isTrigger">Overlap-only collider.</param>
        public static PhysxCollider3DDesc CreateCapsule(
            float radius,
            float halfLength,
            Transform3D? transform = null,
            bool isTrigger = false)
        {
            var baseTransform = transform ?? Transform3D.Zero;
            var sized = baseTransform.WithSize(Size3D.Of(radius, halfLength, 0f));
            return Create(PhysxColliderShape3D.Capsule3D, sized, isTrigger);
        }
    }
}
