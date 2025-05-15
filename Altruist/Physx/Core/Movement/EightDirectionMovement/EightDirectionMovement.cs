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

public class EightDirectionMovementPhysx : AbstractMovementPhysx<EightDirectionMovementPhysxInput>
{
    public override MovementPhysxOutput CalculateMovement(Body body, EightDirectionMovementPhysxInput input)
    {
        // Validate inputs without allocations
        if (input.MoveLeftRight is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(input.MoveLeftRight), "MoveLeftRight must be -1, 0, or 1.");

        if (input.MoveUpDown is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(input.MoveUpDown), "MoveUpDown must be -1, 0, or 1.");

        int dx = input.MoveLeftRight;
        int dy = input.MoveUpDown;

        if (dx == 0 && dy == 0)
        {
            return new MovementPhysxOutput(input.CurrentSpeed, 0.0f, false, Vector2.Zero, Vector2.Zero);
        }

        Vector2 direction;
        if (dx != 0 && dy != 0)
        {
            // Normalize diagonals manually to avoid method call and heap use
            float inv = 1.0f / MathF.Sqrt(2f);
            direction = new Vector2(dx * inv, dy * inv);
        }
        else
        {
            direction = new Vector2(dx, dy);
        }

        float accelerationFactor = input.Turbo ? 2.0f : 1.0f;
        float currentSpeed = input.CurrentSpeed + input.Acceleration * accelerationFactor;
        currentSpeed = Math.Min(currentSpeed, input.MaxSpeed);

        Vector2 velocity = direction * currentSpeed;

        return new MovementPhysxOutput(
            currentSpeed,
            0.0f,
            true,
            velocity,
            Vector2.Zero
        );
    }

    public override float CalculateRotation(Body body, EightDirectionMovementPhysxInput input)
    {
        throw new NotSupportedException("Rotation not supported for EightDirectionMovement");
    }

}