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

namespace Altruist.Physx;

/// <summary>
/// Input for a "forward + rotate" (tank / vehicle style) movement model: the body thrusts along its own facing and turns
/// left/right. Use <see cref="EightDirectionMovementPhysxInput"/> instead when movement follows fixed screen axes.
/// </summary>
public class ForwardMovementPhysxInput : MovementPhysxInput
{
    /// <summary>Thrust along the current facing this tick.</summary>
    public bool MoveForward { get; set; }
    /// <summary>Turn left this tick.</summary>
    public bool RotateLeft { get; set; }
    /// <summary>Turn right this tick.</summary>
    public bool RotateRight { get; set; }
    /// <summary>Boost request; its effect is defined by the movement implementation.</summary>
    public bool Turbo { get; set; }
    /// <summary>Turn rate in radians per second.</summary>
    public float RotationSpeed { get; set; }

}
