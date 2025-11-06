using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{

    public enum PhysxColliderShape3D
    {
        Sphere3D,
        Box3D,
        Capsule3D,
        ConvexHull3D
    }

    public readonly struct PhysxCollisionInfo3D
    {
        public IPhysxCollider3D SelfCollider { get; }
        public IPhysxCollider3D OtherCollider { get; }
        public IPhysxBody3D SelfBody { get; }
        public IPhysxBody3D OtherBody { get; }
        public Vector3 Point { get; }    // contact point on self
        public Vector3 Normal { get; }   // normal pointing out of self
        public float Impulse { get; }    // optional aggregate/normal impulse

        public PhysxCollisionInfo3D(
            IPhysxCollider3D selfCol, IPhysxCollider3D otherCol,
            IPhysxBody3D selfBody, IPhysxBody3D otherBody,
            Vector3 point, Vector3 normal, float impulse)
        {
            SelfCollider = selfCol; OtherCollider = otherCol;
            SelfBody = selfBody; OtherBody = otherBody;
            Point = point; Normal = normal; Impulse = impulse;
        }
    }

    public interface IPhysxCollider3D : IPhysxCollider
    {
        Transform3D Transform { get; set; }
        PhysxColliderShape3D Shape { get; }

        event Action<PhysxCollisionInfo3D>? OnCollisionEnter;
        event Action<PhysxCollisionInfo3D>? OnCollisionStay;
        event Action<PhysxCollisionInfo3D>? OnCollisionExit;
    }


    public readonly struct PhysxRaycastHit3D
    {
        public IPhysxBody3D Body { get; }
        public Vector3 Point { get; }
        public Vector3 Normal { get; }
        public float Fraction { get; }
        public PhysxRaycastHit3D(IPhysxBody3D body, Vector3 point, Vector3 normal, float fraction) { Body = body; Point = point; Normal = normal; Fraction = fraction; }
    }

    public readonly struct PhysxRay3D
    {
        public Vector3 From { get; }
        public Vector3 To { get; }
        public PhysxRay3D(Vector3 from, Vector3 to) { From = from; To = to; }
    }


}