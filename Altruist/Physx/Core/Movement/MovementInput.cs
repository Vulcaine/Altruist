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

/// <summary>
/// Result of one <see cref="IMovementTypePhysx{TInput}"/> calculation: the speed, rotation rate and the
/// 2D velocity/force the caller should apply to the body.
/// </summary>
public record MovementPhysxOutput
{
    /// <summary>Speed after this step, in units per second.</summary>
    public float CurrentSpeed { get; set; }
    /// <summary>Rotation rate produced by this step (radians per second, implementation-defined sign).</summary>
    public float RotationSpeed { get; set; }
    /// <summary>Whether the body is considered moving after this step.</summary>
    public bool Moving { get; set; }

    /// <summary>Linear velocity to set on the body (units per second).</summary>
    public Vector2 Velocity { get; set; }

    /// <summary>Force to apply to the body, if the implementation drives it by force rather than velocity.</summary>
    public Vector2 Force { get; set; }

    /// <summary>Creates a result.</summary>
    /// <param name="currentSpeed">Speed after the step.</param>
    /// <param name="rotationSpeed">Rotation rate.</param>
    /// <param name="moving">Whether the body is moving.</param>
    /// <param name="velocity">Velocity to apply.</param>
    /// <param name="force">Force to apply.</param>
    public MovementPhysxOutput(float currentSpeed, float rotationSpeed, bool moving, Vector2 velocity, Vector2 force)
    {
        CurrentSpeed = currentSpeed;
        RotationSpeed = rotationSpeed;
        Moving = moving;
        Velocity = velocity;
        Force = force;
    }
}

/// <summary>
/// Strategy contract for a legacy input-driven 2D movement model that computes motion for a raw Box2DSharp
/// <see cref="Body"/> from a per-tick input record. This package ships the input types
/// (<see cref="ForwardMovementPhysxInput"/>, <see cref="EightDirectionMovementPhysxInput"/>) but no implementation.
/// </summary>
/// <remarks>
/// Pick the input type by control scheme: <see cref="ForwardMovementPhysxInput"/> for "tank"/vehicle controls (thrust
/// along the facing, rotate left/right); <see cref="EightDirectionMovementPhysxInput"/> for screen-axis controls
/// (up/down/left/right, diagonals by combining two). For new code that works on engine-agnostic bodies prefer the
/// physics-layer helpers (<see cref="Altruist.Physx.TwoD.BodyMotionExtensions2D"/>, or
/// <see cref="Altruist.Physx.ThreeD.BodySteeringExtensions3D"/> in 3D).
/// </remarks>
/// <typeparam name="TInput">Input record type for this movement model.</typeparam>
public interface IMovementTypePhysx<TInput> where TInput : MovementPhysxInput
{
    /// <summary>Computes the motion for one tick while input is active.</summary>
    /// <param name="body">Box2DSharp body being moved (read for its current state).</param>
    /// <param name="input">Input for this tick.</param>
    MovementPhysxOutput CalculateMovement(Body body, TInput input);
    /// <summary>Computes the body's new rotation (radians) for this tick.</summary>
    /// <param name="body">Box2DSharp body being rotated.</param>
    /// <param name="input">Input for this tick.</param>
    float CalculateRotation(Body body, TInput input);
    /// <summary>Computes the motion for one tick while slowing down (no movement input).</summary>
    /// <param name="body">Box2DSharp body being moved.</param>
    /// <param name="input">Input for this tick; <see cref="MovementPhysxInput.Deceleration"/> applies.</param>
    MovementPhysxOutput CalculateDeceleration(Body body, TInput input);
}

/// <summary>Common per-tick parameters for <see cref="IMovementTypePhysx{TInput}"/> movement models.</summary>
public abstract class MovementPhysxInput
{
    /// <summary>Speed before this tick, in units per second.</summary>
    public float CurrentSpeed { get; set; }
    /// <summary>Speed cap in units per second.</summary>
    public float MaxSpeed { get; set; }
    /// <summary>Speed gained per second while accelerating (units/s²).</summary>
    public float Acceleration { get; set; }
    /// <summary>Speed lost per second while decelerating (units/s²).</summary>
    public float Deceleration { get; set; }
    /// <summary>Tick length in seconds. Required: there is no sensible default tick length.</summary>
    public required float DeltaTime { get; set; }
}
