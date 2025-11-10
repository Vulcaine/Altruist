namespace Altruist.Gaming.Movement.TwoD;

public sealed class MovementDriver2D
{
    public object Body { get; }
    public MovementProfile2D Profile { get; set; }
    public MovementState2D State { get; private set; }

    public IMovementPipeline2D Pipeline { get; set; }
    private readonly IPhysxMovementEngine2D _physx;

    public MovementDriver2D(object body, MovementProfile2D profile,
                            MovementState2D initialState, IMovementPipeline2D pipeline, IPhysxMovementEngine2D physx)
    {
        Body = body; Profile = profile; Pipeline = pipeline; _physx = physx;
        State = initialState;
    }

    public void Step(in MovementIntent2D intent, float dt)
    {
        var result = Pipeline.Evaluate(intent, State, Profile, dt);
        _physx.Apply(Body, result, dt);
        // cache State.Velocity/Angle here
        State = State with { Velocity = result.LinearVelocity, AngleRad = State.AngleRad + result.AngularDeltaRad };
    }
}
