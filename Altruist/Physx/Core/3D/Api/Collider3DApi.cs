/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// Engine-agnostic descriptor for a 3D collider.
    /// Providers/engines interpret this to create their own collider objects.
    /// </summary>
    public readonly struct PhysxCollider3DDesc
    {
        /// <summary>Logical id for this collider descriptor.</summary>
        public string Id { get; }

        public PhysxColliderShape3D Shape { get; }
        public Transform3D Transform { get; }
        public bool IsTrigger { get; }

        public HeightfieldData? Heightfield { get; }

        public PhysxCollider3DDesc(string id, PhysxColliderShape3D shape, Transform3D transform, HeightfieldData? heightmap = null, bool isTrigger = false)
        {
            Id = id;
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
            Heightfield = heightmap;
        }
    }

    /// <summary>
    /// Static helpers for building engine-agnostic collider descriptors.
    /// No providers, no engines here.
    /// </summary>
    public static class PhysxCollider3D
    {
        public static PhysxCollider3DDesc Create(
            PhysxColliderShape3D shape,
            Transform3D transform,
            bool isTrigger = false)
        {
            var id = Guid.NewGuid().ToString("N");
            return new PhysxCollider3DDesc(id, shape, transform, isTrigger: isTrigger);
        }

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

        public static PhysxCollider3DDesc CreateSphere(
            float radius,
            Transform3D? transform = null,
            bool isTrigger = false)
        {
            var baseTransform = transform ?? Transform3D.Zero;
            var sized = baseTransform.WithSize(Size3D.Of(radius, 0f, 0f));
            return Create(PhysxColliderShape3D.Sphere3D, sized, isTrigger);
        }

        public static PhysxCollider3DDesc CreateBox(
            Vector3 halfExtents,
            Transform3D? transform = null,
            bool isTrigger = false)
        {
            var baseTransform = transform ?? Transform3D.Zero;
            var sized = baseTransform.WithSize(Size3D.From(halfExtents));
            return Create(PhysxColliderShape3D.Box3D, sized, isTrigger);
        }

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
