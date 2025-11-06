// ColliderApi3D.cs
using System;
using System.Numerics;
using Altruist.Physx.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    public readonly struct PhysxCollider3DParams
    {
        public PhysxColliderShape3D Shape { get; }
        public Transform3D Transform { get; }
        public bool IsTrigger { get; }
        public PhysxCollider3DParams(PhysxColliderShape3D shape, Transform3D transform, bool isTrigger)
        {
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    public readonly struct PhysxConvexHull3DParams
    {
        public ReadOnlyMemory<Vector3> Points { get; }
        public Transform3D Transform { get; }
        public bool IsTrigger { get; }
        public PhysxConvexHull3DParams(ReadOnlyMemory<Vector3> points, Transform3D transform, bool isTrigger)
        {
            Points = points;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    public interface IPhysxColliderApiProvider3D
    {
        IPhysxCollider3D CreateCollider(in PhysxCollider3DParams p);
    }

    public interface IPhysxConvexHull3DProvider
    {
        IPhysxCollider3D CreateConvexHull(in PhysxConvexHull3DParams p);
    }

    public static class PhysxCollider3D
    {
        public static IPhysxColliderApiProvider3D Provider { get; set; } = default!;
        public static IPhysxConvexHull3DProvider? ConvexHullProvider { get; set; }

        public static IPhysxCollider3D Create(PhysxColliderShape3D shape, Transform3D transform, bool isTrigger = false)
            => Provider.CreateCollider(new PhysxCollider3DParams(shape, transform, isTrigger));

        public static IPhysxCollider3D CreateSphere(float radius, Transform3D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform3D.Zero).WithSize(Size3D.Of(radius, 0f, 0f));
            return Provider.CreateCollider(new PhysxCollider3DParams(PhysxColliderShape3D.Sphere3D, t, isTrigger));
        }

        public static IPhysxCollider3D CreateBox(Vector3 halfExtents, Transform3D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform3D.Zero).WithSize(Size3D.From(halfExtents));
            return Provider.CreateCollider(new PhysxCollider3DParams(PhysxColliderShape3D.Box3D, t, isTrigger));
        }

        public static IPhysxCollider3D CreateCapsule(float radius, float halfLength, Transform3D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform3D.Zero).WithSize(Size3D.Of(radius, halfLength, 0f));
            return Provider.CreateCollider(new PhysxCollider3DParams(PhysxColliderShape3D.Capsule3D, t, isTrigger));
        }

        public static IPhysxCollider3D CreateConvexHull(ReadOnlySpan<Vector3> points, Transform3D? transform = null, bool isTrigger = false)
        {
            if (ConvexHullProvider is null)
                throw new InvalidOperationException("PhysxCollider3D.ConvexHullProvider is not set.");
            return ConvexHullProvider.CreateConvexHull(new PhysxConvexHull3DParams(
                points.ToArray(),
                transform ?? Transform3D.Zero,
                isTrigger));
        }
    }
}
