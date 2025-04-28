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
    public MovementPhysxOutput ApplyMovement(Body body, ForwardMovementPhysxInput input)
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

        return new MovementPhysxOutput(input.CurrentSpeed, input.RotationSpeed, posDelta.Length() > 0, body.Position);
    }

    public void ApplyRotation(Body body, ForwardMovementPhysxInput input)
    {
        if (input.RotateLeft)
            body.Rotation -= input.RotationSpeed;
        else if (input.RotateRight)
            body.Rotation += input.RotationSpeed;
    }

    public void ApplyDeceleration(Body body, ForwardMovementPhysxInput movementInput)
    {
        Vector2 direction = MovementHelper.GetDirectionVector(body.Rotation);
        float decelerationForce = -movementInput.Deceleration * movementInput.CurrentSpeed;
        Vector2 force = direction * decelerationForce;
        body.ApplyForce(ref force);

        movementInput.CurrentSpeed = Math.Max(0, movementInput.CurrentSpeed - movementInput.Deceleration);
    }
}