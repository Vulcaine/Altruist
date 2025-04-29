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

public record MovementPhysxOutput
{
    public float CurrentSpeed { get; set; }
    public float RotationSpeed { get; set; }
    public bool Moving { get; set; }
    // public Vector2 Position { get; set; }

    public Vector2 Velocity { get; set; }

    public Vector2 Force { get; set; }

    public MovementPhysxOutput(float currentSpeed, float rotationSpeed, bool moving, Vector2 velocity, Vector2 force) => (CurrentSpeed, RotationSpeed, Moving, Velocity, Force) = (currentSpeed, rotationSpeed, moving, velocity, force);
}

public interface IMovementTypePhysx<TInput> where TInput : MovementPhysxInput
{
    MovementPhysxOutput CalculateMovement(Body body, TInput input);
    float CalculateRotation(Body body, TInput input);
    MovementPhysxOutput CalculateDeceleration(Body body, TInput input);
}

public abstract class MovementPhysxInput
{
    public float CurrentSpeed { get; set; }
    public float MaxSpeed { get; set; }
    public float Acceleration { get; set; }
    public float Deceleration { get; set; }
    public float DeltaTime { get; set; } = 1.0f;
}
