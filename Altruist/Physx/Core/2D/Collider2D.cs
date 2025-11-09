using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    public enum PhysxColliderShape2D
    {
        Circle2D,
        Box2D,
        Capsule2D,
        Polygon2D,
    }


    public readonly struct PhysxCollisionInfo2D
    {
        public IPhysxCollider2D SelfCollider { get; }
        public IPhysxCollider2D OtherCollider { get; }
        public IPhysxBody2D SelfBody { get; }
        public IPhysxBody2D OtherBody { get; }
        public Vector2 Point { get; }
        public Vector2 Normal { get; }
        public float Impulse { get; }

        public PhysxCollisionInfo2D(
            IPhysxCollider2D selfCol, IPhysxCollider2D otherCol,
            IPhysxBody2D selfBody, IPhysxBody2D otherBody,
            Vector2 point, Vector2 normal, float impulse)
        {
            SelfCollider = selfCol; OtherCollider = otherCol;
            SelfBody = selfBody; OtherBody = otherBody;
            Point = point; Normal = normal; Impulse = impulse;
        }
    }


    public interface IPhysxCollider2D : IPhysxCollider
    {
        /// <summary>Local transform relative to the owning body.</summary>
        Transform2D Transform { get; set; }

        /// <summary>Shape discriminator.</summary>
        PhysxColliderShape2D Shape { get; }

        /// <summary>
        /// Optional vertex buffer for polygon colliders (ignored for other shapes).
        /// Return null for non-polygon shapes.
        /// </summary>
        Vector2[]? Vertices { get; }

        event Action<PhysxCollisionInfo2D>? OnCollisionEnter;
        event Action<PhysxCollisionInfo2D>? OnCollisionStay;
        event Action<PhysxCollisionInfo2D>? OnCollisionExit;
    }


    public readonly struct PhysxRaycastHit2D
    {
        public IPhysxBody2D Body { get; }
        public Vector2 Point { get; }
        public Vector2 Normal { get; }
        public float Fraction { get; }
        public PhysxRaycastHit2D(IPhysxBody2D body, Vector2 point, Vector2 normal, float fraction) { Body = body; Point = point; Normal = normal; Fraction = fraction; }
    }

    public readonly struct PhysxRay2D
    {
        public Vector2 From { get; }
        public Vector2 To { get; }
        public PhysxRay2D(Vector2 from, Vector2 to) { From = from; To = to; }
    }

}