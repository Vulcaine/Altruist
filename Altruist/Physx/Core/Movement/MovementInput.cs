/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Numerics;
using Box2DSharp.Dynamics;

namespace Altruist.Physx;

public interface IMovementStats
{
    float CurrentSpeed { get; }
    float MaxSpeed { get; }
    float Acceleration { get; }
    float Deceleration { get; }
    float DeltaTime { get; }
}

public interface IJumpCapability
{
    bool Jump { get; }
    float JumpForce { get; }
    bool IsGrounded { get; }
}

public interface IRotationCapability
{
    float RotationSpeed { get; }

    // -1 = left, 1 = right, 0 = no rotation
    int RotateLeftRight { get; }
}

public interface IDirectionalInput2D
{
    // 0 = none, 1 = right, -1 = left
    int MoveLeftRight { get; }

    // 0 = none, 1 = up, -1 = down
    int MoveUpDown { get; }
}

public interface IForwardMovementInput
{
    bool MoveForward { get; }
}



public record MovementPhysxOutput
{
    public float CurrentSpeed { get; set; }
    public float RotationSpeed { get; set; }
    public bool Moving { get; set; }

    public Vector2 Velocity { get; set; }

    public Vector2 Force { get; set; }

    public MovementPhysxOutput(float currentSpeed, float rotationSpeed, bool moving, Vector2 velocity, Vector2 force)
    {
        CurrentSpeed = currentSpeed;
        RotationSpeed = rotationSpeed;
        Moving = moving;
        Velocity = velocity;
        Force = force;
    }
}

public interface IMovementPhysx<TInput>
{
    MovementPhysxOutput CalculateMovement(Body body, TInput input);
    MovementPhysxOutput CalculateJump(Body body, TInput input);
    float CalculateRotation(Body body, TInput input);
    void StopMovement(Body body);
}


public interface IForcePhysx
{
    void ApplyLinearImpulse(Body body, Vector2 impulse);
    void ApplyForce(Body body, Vector2 force);
    void ClearForces(Body body);
}

public abstract class ForcePhysx : IForcePhysx
{
    public void ApplyLinearImpulse(Body body, Vector2 impulse)
    {
        body.ApplyLinearImpulse(impulse, body.GetWorldCenter(), true);
    }

    public void ApplyForce(Body body, Vector2 force)
    {
        body.ApplyForce(force, body.GetWorldCenter(), true);
    }

    public void ClearForces(Body body)
    {
        body.SetLinearVelocity(VectorConstants.ZeroVector);
    }
}

public class DefaultForcePhysx : ForcePhysx { }

public abstract class AbstractMovementPhysx<TInput> : IMovementPhysx<TInput> where TInput : class
{
    public virtual MovementPhysxOutput CalculateJump(Body body, TInput input)
    {
        if (input is not IJumpCapability jumpInput)
            return new MovementPhysxOutput(0, 0, false, body.LinearVelocity, VectorConstants.ZeroVector);

        if (!jumpInput.Jump || !jumpInput.IsGrounded)
            return new MovementPhysxOutput(0, 0, false, body.LinearVelocity, VectorConstants.ZeroVector);

        var velocity = body.LinearVelocity;

        // Prevent double jump
        if (velocity.Y > 0)
            return new MovementPhysxOutput(0, 0, false, velocity, VectorConstants.ZeroVector);

        float jumpImpulse = body.Mass * jumpInput.JumpForce;
        body.ApplyLinearImpulse(new Vector2(0, jumpImpulse), body.GetWorldCenter(), true);

        return new MovementPhysxOutput(0, 0, false, body.LinearVelocity, VectorConstants.ZeroVector);
    }

    public abstract MovementPhysxOutput CalculateMovement(Body body, TInput input);
    public abstract float CalculateRotation(Body body, TInput input);

    public virtual void StopMovement(Body body)
    {
        body.SetLinearVelocity(VectorConstants.ZeroVector);
    }
}
