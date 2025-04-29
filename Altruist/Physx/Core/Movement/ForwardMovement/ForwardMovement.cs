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
    public virtual MovementPhysxOutput CalculateMovement(Body body, ForwardMovementPhysxInput input)
    {
        float rotationSpeed = CalculateRotation(body, input);

        if (!input.MoveForward)
        {
            return new MovementPhysxOutput(
                input.CurrentSpeed,
                rotationSpeed,
                false,
                VectorConstants.ZeroVector,
                VectorConstants.ZeroVector
            );
        }

        Vector2 direction = new Vector2(MathF.Cos(body.Rotation), MathF.Sin(body.Rotation));
        float accelerationFactor = input.Turbo ? 2.0f : 1.0f;

        float currentSpeed = input.CurrentSpeed + input.Acceleration * accelerationFactor;
        currentSpeed = Math.Min(currentSpeed, input.MaxSpeed);

        Vector2 velocity = direction * currentSpeed;
        Vector2 posDelta = velocity * input.DeltaTime;

        return new MovementPhysxOutput(
            currentSpeed,
            rotationSpeed,
            posDelta.LengthSquared() > 0,
            velocity,
            VectorConstants.ZeroVector
        );
    }

    public virtual float CalculateRotation(Body body, ForwardMovementPhysxInput input)
    {
        return input.RotateLeft ? -input.RotationSpeed : (input.RotateRight ? input.RotationSpeed : 0);
    }

    public virtual MovementPhysxOutput CalculateDeceleration(Body body, ForwardMovementPhysxInput input)
    {
        Vector2 direction = new Vector2(MathF.Cos(body.Rotation), MathF.Sin(body.Rotation));

        float decelerationForce = -input.Deceleration * input.CurrentSpeed;
        Vector2 force = direction * decelerationForce * body.Mass;
        float newSpeed = Math.Max(0, input.CurrentSpeed - input.Deceleration);

        return new MovementPhysxOutput(newSpeed, CalculateRotation(body, input), false, VectorConstants.ZeroVector, force);
    }
}