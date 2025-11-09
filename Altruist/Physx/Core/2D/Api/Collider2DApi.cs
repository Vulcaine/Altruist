// ColliderApi2D.cs

using System.Numerics;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    public readonly struct PhysxCollider2DParams
    {
        public PhysxColliderShape2D Shape { get; }
        public Transform2D Transform { get; }
        public bool IsTrigger { get; }
        public PhysxCollider2DParams(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger)
        {
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    public readonly struct PhysxPolygon2DParams
    {
        public ReadOnlyMemory<Vector2> Vertices { get; }
        public Transform2D Transform { get; }
        public bool IsTrigger { get; }
        public PhysxPolygon2DParams(ReadOnlyMemory<Vector2> vertices, Transform2D transform, bool isTrigger)
        {
            Vertices = vertices;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    public interface IPhysxColliderApiProvider2D
    {
        IPhysxCollider2D CreateCollider(in PhysxCollider2DParams p);
    }

    public interface IPhysxPolygon2DProvider
    {
        IPhysxCollider2D CreatePolygon(in PhysxPolygon2DParams p);
    }

    public static class PhysxCollider2D
    {
        public static IPhysxColliderApiProvider2D Provider { get; set; } = default!;
        public static IPhysxPolygon2DProvider? PolygonProvider { get; set; }

        public static IPhysxCollider2D Create(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger = false)
            => Provider.CreateCollider(new PhysxCollider2DParams(shape, transform, isTrigger));

        public static IPhysxCollider2D CreateCircle(float radius, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, 0f));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Circle2D, t, isTrigger));
        }

        public static IPhysxCollider2D CreateRectangle(Vector2 halfExtents, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.From(halfExtents));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Box2D, t, isTrigger));
        }

        public static IPhysxCollider2D CreateCapsule(float radius, float halfLength, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, halfLength));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Capsule2D, t, isTrigger));
        }

        public static IPhysxCollider2D CreatePolygon(ReadOnlySpan<Vector2> vertices, Transform2D? transform = null, bool isTrigger = false)
        {
            if (PolygonProvider is null)
                throw new InvalidOperationException("PhysxCollider2D.PolygonProvider is not set.");
            return PolygonProvider.CreatePolygon(new PhysxPolygon2DParams(
                vertices.ToArray(),
                transform ?? Transform2D.Zero,
                isTrigger));
        }
    }
}
