using System.Numerics;

using Altruist.Physx.ThreeD;

namespace Altruist.Physx;

public interface IPhysxForceApi3D
{
    /// <summary>Add a continuous force this frame (scaled by fixed dt in engine).</summary>
    void Apply(IPhysxBody3D body, Vector3 force);

    /// <summary>Add an instantaneous linear impulse.</summary>
    void Impulse(IPhysxBody3D body, Vector3 impulse);

    /// <summary>Add an instantaneous angular impulse (torque).</summary>
    void Torque(IPhysxBody3D body, Vector3 torque);

    /// <summary>Set linear velocity via PhysxForce (for engines that prefer that path).</summary>
    void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity);

    /// <summary>Set angular velocity via PhysxForce.</summary>
    void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity);
}

public interface IPhysxMotionApi3D
{
    Vector3 GetLinearVelocity(IPhysxBody3D body);
    Vector3 GetAngularVelocity(IPhysxBody3D body);

    void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity);
    void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity);
}

public interface IPhysxTransformApi3D
{
    void Teleport(IPhysxBody3D body, Vector3 position, Quaternion orientation, bool clearVelocities = false);

    (Vector3 position, Quaternion orientation) GetPose(IPhysxBody3D body);
}

public interface IPhysxApiProvider3D
{
    IPhysxForceApi3D Force { get; }
    IPhysxMotionApi3D Motion { get; }
    IPhysxTransformApi3D Transform { get; }
}
