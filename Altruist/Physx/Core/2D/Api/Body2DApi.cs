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

        // Rigid-body members. Engine-backed bodies implement them natively; the defaults keep
        // lightweight implementations (fakes, kinematic stand-ins) working.

        /// <summary>Game data carried by the body (a tag, the owning entity, ...).</summary>
        object? UserData { get => null; set { } }

        /// <summary>A sleeping body is skipped by the solver until something wakes it.</summary>
        bool IsAwake { get => true; set { } }

        /// <summary>A disabled body takes no part in the simulation (no contacts, no motion).</summary>
        bool IsEnabled { get => true; set { } }

        /// <summary>Center of mass in world coordinates.</summary>
        Vector2 WorldCenter => Position;

        /// <summary>Moves and rotates the body in one call (radians).</summary>
        void SetTransform(Vector2 position, float angle)
        {
            Position = position;
            RotationZ = angle;
        }

        /// <summary>A body-local direction in world coordinates.</summary>
        Vector2 GetWorldVector(Vector2 local) => Rotation2D.FromRadians(RotationZ).Rotate(local);

        /// <summary>A world direction in body-local coordinates.</summary>
        Vector2 GetLocalVector(Vector2 world) => Rotation2D.FromRadians(RotationZ).Unrotate(world);

        /// <summary>A body-local point in world coordinates.</summary>
        Vector2 GetWorldPoint(Vector2 local) => Position + GetWorldVector(local);

        /// <summary>A world point in body-local coordinates.</summary>
        Vector2 GetLocalPoint(Vector2 world) => GetLocalVector(world - Position);
    }


    public interface IPhysxWorld2D : IPhysxWorld
    {
        void AddBody(IPhysxBody2D body);
        void RemoveBody(IPhysxBody body);
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
