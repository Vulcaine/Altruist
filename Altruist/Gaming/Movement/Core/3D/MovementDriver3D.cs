// MovementDriver3D.cs
using System.Numerics;

namespace Altruist.Gaming.Movement.ThreeD;

public sealed class MovementDriver3D
{
    public object Body { get; }
    public MovementProfile3D Profile { get; set; }
    public MovementState3D State { get; private set; }

    public IMovementPipeline3D Pipeline { get; set; }
    private readonly IPhysxMovementEngine3D _physx;

    public MovementDriver3D(object body, MovementProfile3D profile,
                            MovementState3D initialState, IMovementPipeline3D pipeline, IPhysxMovementEngine3D physx)
    {
        Body = body;
        Profile = profile;
        Pipeline = pipeline;
        _physx = physx;
        State = initialState;
    }

    public void Step(in MovementIntent3D intent, float dt)
    {
        var result = Pipeline.Evaluate(intent, State, Profile, dt);
        _physx.Apply(Body, result, dt);

        // Integrate orientation from Euler deltas (ZYX order: yaw, pitch, roll)
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, result.AngularDeltaEuler.Y);
        var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, result.AngularDeltaEuler.X);
        var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, result.AngularDeltaEuler.Z);
        var delta = Quaternion.Normalize(roll * pitch * yaw);
        var orientation = Quaternion.Normalize(delta * State.Orientation);

        if (orientation.Y != float.NaN && orientation.X != float.NaN && orientation.Z != float.NaN)
        {
            State = State with
            {
                Velocity = result.LinearVelocity,
                Orientation = Quaternion.Normalize(delta * State.Orientation)
            };
        }
    }
}
