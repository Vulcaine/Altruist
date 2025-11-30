using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD;

[Service(typeof(IPhysxApiProvider3D))]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
public sealed class BepuPhysxApiProvider3D : IPhysxApiProvider3D
{
    public IPhysxForceApi3D Force { get; }
    public IPhysxMotionApi3D Motion { get; }
    public IPhysxTransformApi3D Transform { get; }

    public BepuPhysxApiProvider3D()
    {
        Force = new BepuPhysxForceApi3D();
        Motion = new BepuPhysxMotionApi3D();
        Transform = new BepuPhysxTransformApi3D();
    }
}
internal sealed class BepuPhysxForceApi3D : IPhysxForceApi3D
{
    public void Apply(IPhysxBody3D body, Vector3 force)
    {
        var f = PhysxForce.Force3D(force);
        body.ApplyForce(in f);
    }

    public void Impulse(IPhysxBody3D body, Vector3 impulse)
    {
        var f = PhysxForce.Impulse3D(impulse);
        body.ApplyForce(in f);
    }

    public void Torque(IPhysxBody3D body, Vector3 torque)
    {
        var f = PhysxForce.Torque3D(torque);
        body.ApplyForce(in f);
    }

    public void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity)
    {
        var f = PhysxForce.LinearVelocity3D(velocity);
        body.ApplyForce(in f);
    }

    public void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity)
    {
        var f = PhysxForce.AngularVelocity3D(angularVelocity);
        body.ApplyForce(in f);
    }
}

internal sealed class BepuPhysxMotionApi3D : IPhysxMotionApi3D
{
    public Vector3 GetLinearVelocity(IPhysxBody3D body) => body.LinearVelocity;
    public Vector3 GetAngularVelocity(IPhysxBody3D body) => body.AngularVelocity;

    public void SetLinearVelocity(IPhysxBody3D body, Vector3 velocity)
    {
        body.LinearVelocity = velocity;
    }

    public void SetAngularVelocity(IPhysxBody3D body, Vector3 angularVelocity)
    {
        body.AngularVelocity = angularVelocity;
    }
}

internal sealed class BepuPhysxTransformApi3D : IPhysxTransformApi3D
{
    public void Teleport(IPhysxBody3D body, Vector3 position, Quaternion orientation, bool clearVelocities = false)
    {
        body.Position = position;
        body.Rotation = orientation;

        if (clearVelocities)
        {
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
        }
    }

    public (Vector3 position, Quaternion orientation) GetPose(IPhysxBody3D body)
    {
        return (body.Position, body.Rotation);
    }
}
