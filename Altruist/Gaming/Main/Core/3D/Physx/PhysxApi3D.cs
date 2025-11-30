using System.Numerics;

using Altruist.Physx;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD;

public interface IWorldPhysics3D
{
    IWorldForceApi3D Force { get; }
    IWorldMotionApi3D Motion { get; }
    IWorldTransformApi3D Transform { get; }
}

public interface IWorldForceApi3D
{
    void Apply(IWorldObject3D obj, Vector3 force);
    void Impulse(IWorldObject3D obj, Vector3 impulse);
    void Torque(IWorldObject3D obj, Vector3 torque);

    void SetLinearVelocity(IWorldObject3D obj, Vector3 velocity);
    void SetAngularVelocity(IWorldObject3D obj, Vector3 angularVelocity);
}

public interface IWorldMotionApi3D
{
    Vector3 GetLinearVelocity(IWorldObject3D obj);
    Vector3 GetAngularVelocity(IWorldObject3D obj);

    void SetLinearVelocity(IWorldObject3D obj, Vector3 velocity);
    void SetAngularVelocity(IWorldObject3D obj, Vector3 angularVelocity);
}

public interface IWorldTransformApi3D
{
    void Teleport(IWorldObject3D obj, Vector3 position, Quaternion orientation, bool clearVelocities = false);

    (Vector3 position, Quaternion orientation) GetPose(IWorldObject3D obj);
}

[Service(typeof(IWorldPhysics3D))]
public sealed class WorldPhysics3D : IWorldPhysics3D
{
    public IWorldForceApi3D Force { get; }
    public IWorldMotionApi3D Motion { get; }
    public IWorldTransformApi3D Transform { get; }

    public WorldPhysics3D(IPhysxApiProvider3D physx)
    {
        Force = new WorldForceApi3D(physx.Force);
        Motion = new WorldMotionApi3D(physx.Motion);
        Transform = new WorldTransformApi3D(physx.Transform);
    }

    /// <summary>
    /// Helper to unify how we get the underlying body from a world object.
    /// </summary>
    internal static IPhysxBody3D? GetBody(IWorldObject3D obj)
    {
        // If you have an interface like IHasPhysxBody3D, use that.
        if (obj is WorldObjectPrefab3D prefab)
            return prefab.Body;

        // Future-proof: if other world object types also store Body, adapt here.

        return null;
    }

    private sealed class WorldForceApi3D : IWorldForceApi3D
    {
        private readonly IPhysxForceApi3D _force;

        public WorldForceApi3D(IPhysxForceApi3D force)
        {
            _force = force;
        }

        public void Apply(IWorldObject3D obj, Vector3 force)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _force.Apply(body, force);
        }

        public void Impulse(IWorldObject3D obj, Vector3 impulse)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _force.Impulse(body, impulse);
        }

        public void Torque(IWorldObject3D obj, Vector3 torque)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _force.Torque(body, torque);
        }

        public void SetLinearVelocity(IWorldObject3D obj, Vector3 velocity)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _force.SetLinearVelocity(body, velocity);
        }

        public void SetAngularVelocity(IWorldObject3D obj, Vector3 angularVelocity)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _force.SetAngularVelocity(body, angularVelocity);
        }
    }

    private sealed class WorldMotionApi3D : IWorldMotionApi3D
    {
        private readonly IPhysxMotionApi3D _motion;

        public WorldMotionApi3D(IPhysxMotionApi3D motion)
        {
            _motion = motion;
        }

        public Vector3 GetLinearVelocity(IWorldObject3D obj)
        {
            var body = GetBody(obj);
            return body == null ? Vector3.Zero : _motion.GetLinearVelocity(body);
        }

        public Vector3 GetAngularVelocity(IWorldObject3D obj)
        {
            var body = GetBody(obj);
            return body == null ? Vector3.Zero : _motion.GetAngularVelocity(body);
        }

        public void SetLinearVelocity(IWorldObject3D obj, Vector3 velocity)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _motion.SetLinearVelocity(body, velocity);
        }

        public void SetAngularVelocity(IWorldObject3D obj, Vector3 angularVelocity)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _motion.SetAngularVelocity(body, angularVelocity);
        }
    }

    private sealed class WorldTransformApi3D : IWorldTransformApi3D
    {
        private readonly IPhysxTransformApi3D _transform;

        public WorldTransformApi3D(IPhysxTransformApi3D transform)
        {
            _transform = transform;
        }

        public void Teleport(IWorldObject3D obj, Vector3 position, Quaternion orientation, bool clearVelocities = false)
        {
            var body = GetBody(obj);
            if (body == null)
                return;
            _transform.Teleport(body, position, orientation, clearVelocities);
        }

        public (Vector3 position, Quaternion orientation) GetPose(IWorldObject3D obj)
        {
            var body = GetBody(obj);
            if (body == null)
                return (default, Quaternion.Identity);

            return _transform.GetPose(body);
        }
    }
}
