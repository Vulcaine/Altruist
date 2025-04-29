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

using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace Altruist.Physx;

public class EightDirectionMovementPhysx : IMovementTypePhysx<EightDirectionMovementPhysxInput>
{
    public MovementPhysxOutput CalculateMovement(Body body, EightDirectionMovementPhysxInput input)
    {
        bool moving = input.MoveUp || input.MoveDown || input.MoveLeft || input.MoveRight;
        var zeroVector = Vector2.Zero;

        if (!moving)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, zeroVector, zeroVector);
        }

        Vector2 direction = zeroVector;
        if (input.MoveUp) direction += new Vector2(0, -1);
        if (input.MoveDown) direction += new Vector2(0, 1);
        if (input.MoveLeft) direction += new Vector2(-1, 0);
        if (input.MoveRight) direction += new Vector2(1, 0);

        if (direction == zeroVector)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, zeroVector, zeroVector);
        }

        direction.Normalize();

        float accelerationFactor = input.Turbo ? 2.0f : 1.0f;
        var currentSpeed = input.CurrentSpeed + input.Acceleration * accelerationFactor;
        currentSpeed = Math.Min(input.CurrentSpeed, input.MaxSpeed);

        var posDelta = direction * currentSpeed;
        Vector2 velocity = posDelta;

        return new MovementPhysxOutput(currentSpeed, 0.0f, posDelta.LengthSquared() > 0, velocity, zeroVector);
    }

    public virtual float CalculateRotation(Body body, EightDirectionMovementPhysxInput input)
    {
        throw new NotSupportedException("Rotation not supported for EightDirectionMovement");
    }

    public virtual MovementPhysxOutput CalculateDeceleration(Body body, EightDirectionMovementPhysxInput input)
    {
        Vector2 velocity = body.LinearVelocity;
        Vector2 force = Vector2.Zero;
        if (velocity.LengthSquared() > 0)
        {
            // Normalize the velocity to get the direction of deceleration
            Vector2 decelerationDirection = Vector2.Normalize(velocity);

            // Calculate the deceleration force magnitude
            float decelerationForceMagnitude = input.Deceleration * input.CurrentSpeed;

            // Calculate the deceleration force vector
            force = -decelerationDirection * decelerationForceMagnitude * body.Mass;
            // // Apply the calculated deceleration force to the body
            // body.ApplyForce(ref force);
        }

        // Update the current speed after applying deceleration
        var currSpeed = Math.Max(0, input.CurrentSpeed - input.Deceleration);
        return new MovementPhysxOutput(currSpeed, 0.0f, false, Vector2.Zero, force);
    }

}