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

public class ForwardMovementPhysx : IMovementTypePhysx<ForwardMovementPhysxInput>
{
    public virtual MovementPhysxOutput ApplyMovement(Body body, ForwardMovementPhysxInput input)
    {
        if (!input.MoveForward) return new MovementPhysxOutput(input.CurrentSpeed, input.RotationSpeed, false, body.Position);

        Vector2 direction = MovementHelper.GetDirectionVector(body.Rotation);
        float accelerationFactor = input.Turbo ? 2.0f : 1.0f;
        var currentSpeed = input.CurrentSpeed;
        currentSpeed += input.Acceleration * accelerationFactor;
        currentSpeed = Math.Min(currentSpeed, input.MaxSpeed);

        Vector2 velocity = direction * currentSpeed;
        body.LinearVelocity = velocity;

        var posDelta = velocity * input.DeltaTime;
        body.Position += posDelta;

        return new MovementPhysxOutput(currentSpeed, input.RotationSpeed, posDelta.Length() > 0, body.Position);
    }

    public virtual void ApplyRotation(Body body, ForwardMovementPhysxInput input)
    {
        if (input.RotateLeft)
            body.Rotation -= input.RotationSpeed;
        else if (input.RotateRight)
            body.Rotation += input.RotationSpeed;
    }

    public virtual MovementPhysxOutput ApplyDeceleration(Body body, ForwardMovementPhysxInput movementInput)
    {
        // Get the direction of movement based on the body's rotation
        Vector2 direction = MovementHelper.GetDirectionVector(body.Rotation);

        // Calculate the deceleration force based on the current speed and deceleration rate
        float decelerationForce = -movementInput.Deceleration * movementInput.CurrentSpeed;

        // Calculate the force vector
        Vector2 force = direction * decelerationForce * body.Mass;

        // Apply the deceleration force over time
        body.ApplyForce(ref force);

        // Update the current speed after applying deceleration
        float newSpeed = Math.Max(0, movementInput.CurrentSpeed - movementInput.Deceleration);

        // Return the result with the new speed and the body's current position
        return new MovementPhysxOutput(newSpeed, movementInput.RotationSpeed, false, body.Position);
    }
}