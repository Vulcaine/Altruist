// BodyApi.cs
using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{

    public interface IPhysxBody2D : IPhysxBody
    {
        // Existing members (examples)
        Vector2 Position { get; set; }

        Vector2 LinearVelocity { get; set; }

        float AngularVelocityZ { get; set; }

        float RotationZ { get; set; }

    }


    public interface IPhysxWorld2D : IPhysxWorld
    {
        IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1);
    }


    public interface IPhysxBodyApiProvider2D
    {
        IPhysxBody2D CreateBody(PhysxBodyType type, float mass, Transform2D transform);

        /// <summary>Attach a collider to a body (creates a fixture under the hood).</summary>
        void AddCollider(IPhysxBody2D body, IPhysxCollider2D collider);

        /// <summary>Detach and destroy the collider’s fixture if attached.</summary>
        void RemoveCollider(IPhysxCollider2D collider);
    }

    public static class PhysxBody2D
    {
        public static IPhysxBodyApiProvider2D Provider { get; set; } = default!;

        public static IPhysxBody2D Create(float mass, Transform2D transform) =>
            Provider.CreateBody(mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static, mass, transform);

        public static IPhysxBody2D Create(PhysxBodyType type, float mass, Transform2D transform) =>
            Provider.CreateBody(type, mass, transform);
    }
}
