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

using Box2DSharp.Dynamics;
using System.Numerics;

namespace Altruist.Physx;

public class ForwardMovementPhysx : AbstractMovementPhysx<ForwardMovementPhysxInput>
{
    public override MovementPhysxOutput CalculateMovement(Body body, ForwardMovementPhysxInput input)
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

        float angle = body.GetAngle();
        Vector2 direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
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

    public override float CalculateRotation(Body body, ForwardMovementPhysxInput input)
    {
        // Rotation is handled via RotateLeftRight vector:
        // - X represents "rotate left", Y represents "rotate right"
        // - Each component is either 0 or 1, indicating rotation intent
        // - Both directions cannot be active simultaneously (conflict results in no rotation)
        if ((input.RotateLeftRight.X != 0f && input.RotateLeftRight.X != 1f) ||
            (input.RotateLeftRight.Y != 0f && input.RotateLeftRight.Y != 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(input.RotateLeftRight),
                "RotateLeftRight values must be either 0 or 1.");
        }

        return input.RotationSpeed * (input.RotateLeftRight.Y - input.RotateLeftRight.X);
    }
}