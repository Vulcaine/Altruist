using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{

    /// <summary>
    /// Primitive shape of a 3D collider. How <see cref="IPhysxCollider3D.Transform"/>'s <c>Size</c> is read depends on the shape
    /// (see the <see cref="PhysxCollider3D"/> factory methods).
    /// </summary>
    public enum PhysxColliderShape3D
    {
        /// <summary>Sphere; <c>Size.X</c> is the radius.</summary>
        Sphere3D,
        /// <summary>Box; <c>Size</c> holds the half extents (X, Y, Z).</summary>
        Box3D,
        /// <summary>Capsule aligned with the local Y axis; <c>Size.X</c> is the radius, <c>Size.Y</c> the half length of the inner segment.</summary>
        Capsule3D,
        /// <summary>Terrain built from <see cref="IPhysxCollider3D.Heightfield"/> (triangle mesh in the BEPU backend).</summary>
        Heightfield3D
    }

    /// <summary>Contact data passed to <see cref="IPhysxCollider3D"/> collision events, seen from the "self" collider.</summary>
    public readonly struct PhysxCollisionInfo3D
    {
        /// <summary>Collider that raised the event.</summary>
        public IPhysxCollider3D SelfCollider { get; }
        /// <summary>The other collider in the contact.</summary>
        public IPhysxCollider3D OtherCollider { get; }
        /// <summary>Body owning <see cref="SelfCollider"/>.</summary>
        public IPhysxBody3D SelfBody { get; }
        /// <summary>Body owning <see cref="OtherCollider"/>.</summary>
        public IPhysxBody3D OtherBody { get; }
        /// <summary>World-space contact point on the self collider.</summary>
        public Vector3 Point { get; }    // contact point on self
        /// <summary>Contact normal pointing out of the self collider (world space).</summary>
        public Vector3 Normal { get; }   // normal pointing out of self
        /// <summary>Optional aggregate/normal impulse of the contact; 0 when the backend does not provide it.</summary>
        public float Impulse { get; }    // optional aggregate/normal impulse

        /// <summary>Creates a contact record.</summary>
        /// <param name="selfCol">Collider raising the event.</param>
        /// <param name="otherCol">Other collider.</param>
        /// <param name="selfBody">Body of <paramref name="selfCol"/>.</param>
        /// <param name="otherBody">Body of <paramref name="otherCol"/>.</param>
        /// <param name="point">World-space contact point on self.</param>
        /// <param name="normal">Normal pointing out of self.</param>
        /// <param name="impulse">Contact impulse, or 0.</param>
        public PhysxCollisionInfo3D(
            IPhysxCollider3D selfCol, IPhysxCollider3D otherCol,
            IPhysxBody3D selfBody, IPhysxBody3D otherBody,
            Vector3 point, Vector3 normal, float impulse)
        {
            SelfCollider = selfCol;
            OtherCollider = otherCol;
            SelfBody = selfBody;
            OtherBody = otherBody;
            Point = point;
            Normal = normal;
            Impulse = impulse;
        }
    }

    /// <summary>
    /// 3D collider: a shape description that is attached to an <see cref="IPhysxBody3D"/> through
    /// <see cref="IPhysxBodyApiProvider3D.AddCollider"/>. Create instances with
    /// <see cref="IPhysxColliderApiProvider3D.CreateCollider"/> from a <see cref="PhysxCollider3D"/> descriptor.
    /// </summary>
    /// <remarks>
    /// In the BEPU backend the collider is data-only: its shape and <c>Transform.Size</c> are read once when attached
    /// (the transform's position/rotation offset is not applied), and the collision/trigger events declared here are
    /// currently never raised. For overlap-driven gameplay use <see cref="CollisionHandlerAttribute"/> handlers instead.
    /// </remarks>
    public interface IPhysxCollider3D : IPhysxCollider
    {
        /// <summary>Shape transform; <c>Size</c> encodes the shape dimensions (see <see cref="PhysxColliderShape3D"/>).</summary>
        Transform3D Transform { get; set; }
        /// <summary>Primitive shape kind.</summary>
        PhysxColliderShape3D Shape { get; }

        /// <summary>Height samples for <see cref="PhysxColliderShape3D.Heightfield3D"/> colliders; <see langword="null"/> for other shapes.</summary>
        HeightfieldData? Heightfield { get; set; }

        /// <summary>Raised when this collider starts touching another collider (backend permitting).</summary>
        event Action<PhysxCollisionInfo3D>? OnCollisionEnter;
        /// <summary>Raised every step while this collider keeps touching another collider (backend permitting).</summary>
        event Action<PhysxCollisionInfo3D>? OnCollisionStay;
        /// <summary>Raised when this collider stops touching another collider (backend permitting).</summary>
        event Action<PhysxCollisionInfo3D>? OnCollisionExit;
    }

    /// <summary>One hit returned by <see cref="IPhysxWorldEngine3D.RayCast"/> or <see cref="IPhysxWorldEngine3D.CapsuleCast"/>.</summary>
    public readonly struct PhysxRaycastHit3D
    {
        /// <summary>Body that was hit.</summary>
        public IPhysxBody3D Body { get; }
        /// <summary>World-space hit point (zero for a sweep that starts already overlapping).</summary>
        public Vector3 Point { get; }
        /// <summary>Surface normal at the hit (zero for a sweep that starts already overlapping).</summary>
        public Vector3 Normal { get; }
        /// <summary>Distance along the cast direction from the origin to the hit, in world units (0 when starting inside).</summary>
        public float T { get; }
        /// <summary>Creates a hit record.</summary>
        /// <param name="body">Body that was hit.</param>
        /// <param name="point">World-space hit point.</param>
        /// <param name="normal">Surface normal.</param>
        /// <param name="t">Distance along the cast.</param>
        public PhysxRaycastHit3D(IPhysxBody3D body, Vector3 point, Vector3 normal, float t) { Body = body; Point = point; Normal = normal; T = t; }
    }

    /// <summary>Finite ray segment from <see cref="From"/> to <see cref="To"/> (world space); the cast length is the segment length.</summary>
    public readonly struct PhysxRay3D
    {
        /// <summary>Start point.</summary>
        public Vector3 From { get; }
        /// <summary>End point; hits beyond it are not reported.</summary>
        public Vector3 To { get; }
        /// <summary>Creates a ray segment.</summary>
        /// <param name="from">Start point.</param>
        /// <param name="to">End point.</param>
        public PhysxRay3D(Vector3 from, Vector3 to) { From = from; To = to; }
    }


}
