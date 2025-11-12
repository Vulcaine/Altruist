
using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    public interface IPhysxBody3D : IPhysxBody
    {
        Vector3 Position { get; set; }
        Quaternion Rotation { get; set; }
        Vector3 LinearVelocity { get; set; }
        Vector3 AngularVelocity { get; set; }
    }

    public interface IPhysxBodyApiProvider3D
    {
        IPhysxBody3D CreateBody(PhysxBodyType type, float mass, Transform3D transform);

        /// <summary>Attach a collider to a body (creates a fixture under the hood).</summary>
        void AddCollider(IPhysxBody3D body, IPhysxCollider3D collider);

        /// <summary>Detach and destroy the collider’s fixture if attached.</summary>
        void RemoveCollider(IPhysxCollider3D collider);
    }

    public static class PhysxBody3D
    {
        public static IPhysxBodyApiProvider3D Provider { get; set; } = default!;

        public static IPhysxBody3D Create(float mass, Size3D size, Position3D position)
            => Provider.CreateBody(
                mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static,
                mass,
                new Transform3D(position, size, Scale3D.One, Rotation3D.Identity));

        public static IPhysxBody3D Create(PhysxBodyType type, float mass, Transform3D transform)
            => Provider.CreateBody(type, mass, transform);
    }
}
