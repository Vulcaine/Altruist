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

public class EightDirectionMovementPhysx : AbstractMovementTypePhysx<EightDirectionMovementPhysxInput>
{
    public override MovementPhysxOutput CalculateMovement(Body body, EightDirectionMovementPhysxInput input)
    {
        var zeroVector = VectorConstants.ZeroVector;

        // Movement is controlled via two directional vectors:
        // - MoveUpDownVector, MovementLeftRightVector: X represents "up", Y represents "down"
        // - Each component is either 0 or 1, indicating active movement in that direction
        if ((input.MoveLeftRightVector.X != 0f && input.MoveLeftRightVector.X != 1f) ||
            (input.MoveLeftRightVector.Y != 0f && input.MoveLeftRightVector.Y != 1f) ||
            (input.MoveUpDownVector.X != 0f && input.MoveUpDownVector.X != 1f) ||
            (input.MoveUpDownVector.Y != 0f && input.MoveUpDownVector.Y != 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(input),
                "Movement vector components must be either 0 or 1.");
        }

        Vector2 direction = input.MoveLeftRightVector + input.MoveUpDownVector;

        if (direction == zeroVector)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, zeroVector, zeroVector);
        }

        direction = Vector2.Normalize(direction);

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

    public override float CalculateRotation(Body body, EightDirectionMovementPhysxInput input)
    {
        throw new NotSupportedException("Rotation not supported for EightDirectionMovement");
    }

    // public virtual MovementPhysxOutput CalculateDeceleration(Body body, EightDirectionMovementPhysxInput input)
    // {
    //     Vector2 velocity = body.LinearVelocity;
    //     Vector2 force = VectorConstants.ZeroVector;

    //     if (velocity.LengthSquared() > 0)
    //     {
    //         Vector2 decelerationDirection = velocity;
    //         decelerationDirection.Normalize();

    //         float decelerationForceMagnitude = input.Deceleration * input.CurrentSpeed;
    //         force = -decelerationDirection * decelerationForceMagnitude * body.Mass;
    //     }

    //     float currSpeed = Math.Max(0, input.CurrentSpeed - input.Deceleration);
    //     return new MovementPhysxOutput(currSpeed, 0.0f, false, VectorConstants.ZeroVector, force);
    // }
}