using System.Numerics;

namespace Altruist.Physx.Contracts
{
    public enum PhysxBodyType { Static, Dynamic, Kinematic }


    public readonly struct PhysxForce
    {
        public enum Kind
        {
            AddForce2D,
            AddForce3D,
            AddImpulse2D,
            AddImpulse3D,
            AddTorque2D,
            AddTorque3D,
            SetLinearVelocity2D,
            SetLinearVelocity3D,
            SetAngularVelocity2D,
            SetAngularVelocity3D
        }

        public Kind Type { get; }
        public Vector3 Vector { get; }

        private PhysxForce(Kind type, Vector3 vec)
        {
            Type = type;
            Vector = vec;
        }

        public static PhysxForce Force2D(Vector2 f) => new(Kind.AddForce2D, new Vector3(f, 0));
        public static PhysxForce Impulse2D(Vector2 j) => new(Kind.AddImpulse2D, new Vector3(j, 0));
        public static PhysxForce Torque2D(float tauZ) => new(Kind.AddTorque2D, new Vector3(0, 0, tauZ));
        public static PhysxForce LinearVelocity2D(Vector2 v) => new(Kind.SetLinearVelocity2D, new Vector3(v, 0));
        public static PhysxForce AngularVelocity2D(float wZ) => new(Kind.SetAngularVelocity2D, new Vector3(0, 0, wZ));

        public static PhysxForce Force3D(Vector3 f) => new(Kind.AddForce3D, f);
        public static PhysxForce Impulse3D(Vector3 j) => new(Kind.AddImpulse3D, j);
        public static PhysxForce Torque3D(Vector3 tau) => new(Kind.AddTorque3D, tau);
        public static PhysxForce LinearVelocity3D(Vector3 v) => new(Kind.SetLinearVelocity3D, v);
        public static PhysxForce AngularVelocity3D(Vector3 w) => new(Kind.SetAngularVelocity3D, w);
    }

    public interface IPhysxBody
    {
        string Id { get; }
        PhysxBodyType Type { get; set; }
        float Mass { get; set; }

        void AddCollider(IPhysxCollider collider);
        bool RemoveCollider(IPhysxCollider collider);
        ReadOnlySpan<IPhysxCollider> GetColliders();
        void ApplyForce(in PhysxForce force);
        object? UserData { get; set; }

        bool TryGetColliderById(string colliderId, out IPhysxCollider collider);

        IPhysxCollider? GetColliderAt(int index);
    }

    public interface IPhysxWorld
    {
        void Step(float deltaTime);
    }


}
