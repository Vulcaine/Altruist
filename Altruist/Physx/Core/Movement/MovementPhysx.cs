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

public class VectorConstants
{
    public static readonly Vector2 ZeroVector = Vector2.Zero;
    public static readonly Vector2 LeftDirection = new Vector2(-1, 0);
    public static readonly Vector2 RightDirection = new Vector2(1, 0);
}

public class MovementPhysx
{
    public ForwardMovementPhysx Forward { get; }
    public EightDirectionMovementPhysx EightDirection { get; }

    public MovementPhysx()
    {
        Forward = new ForwardMovementPhysx();
        EightDirection = new EightDirectionMovementPhysx();
    }

    public void ApplyMovement(Body body, MovementPhysxOutput input)
    {
        body.LinearVelocity = input.Velocity;
        body.Rotation += input.RotationSpeed;

        if (input.Force != Vector2.Zero)
        {
            body.ApplyForce(input.Force);
        }
    }
}