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
using Box2DSharp.Common;
using Box2DSharp.Dynamics;

namespace Altruist.Physx;

public class EightDirectionMovementPhysx : IMovementTypePhysx<EightDirectionMovementPhysxInput>
{
    public MovementPhysxOutput CalculateMovement(Body body, EightDirectionMovementPhysxInput input)
    {
        bool moving = input.MoveUp || input.MoveDown || input.MoveLeft || input.MoveRight;
        var zeroVector = VectorConstants.ZeroVector;

        if (!moving)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, zeroVector, zeroVector);
        }

        Vector2 direction = zeroVector;
        if (input.MoveUp) direction += VectorConstants.UpDirection;
        if (input.MoveDown) direction += VectorConstants.DownDirection;
        if (input.MoveLeft) direction += VectorConstants.LeftDirection;
        if (input.MoveRight) direction += VectorConstants.RightDirection;

        if (direction.X == 0 && direction.Y == 0)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, zeroVector, zeroVector);
        }

        direction.Normalize();

        float accelerationFactor = input.Turbo ? 2.0f : 1.0f;
        float currentSpeed = input.CurrentSpeed + input.Acceleration * accelerationFactor;
        currentSpeed = Math.Min(currentSpeed, input.MaxSpeed);

        Vector2 velocity = direction * currentSpeed;

        return new MovementPhysxOutput(
            currentSpeed,
            0.0f,
            velocity.LengthSquared() > 0,
            velocity,
            zeroVector
        );
    }

    public virtual float CalculateRotation(Body body, EightDirectionMovementPhysxInput input)
    {
        throw new NotSupportedException("Rotation not supported for EightDirectionMovement");
    }

    public virtual MovementPhysxOutput CalculateDeceleration(Body body, EightDirectionMovementPhysxInput input)
    {
        Vector2 velocity = body.LinearVelocity;
        Vector2 force = VectorConstants.ZeroVector;

        if (velocity.LengthSquared() > 0)
        {
            Vector2 decelerationDirection = velocity;
            decelerationDirection.Normalize();

            float decelerationForceMagnitude = input.Deceleration * input.CurrentSpeed;
            force = -decelerationDirection * decelerationForceMagnitude * body.Mass;
        }

        float currSpeed = Math.Max(0, input.CurrentSpeed - input.Deceleration);
        return new MovementPhysxOutput(currSpeed, 0.0f, false, VectorConstants.ZeroVector, force);
    }
}