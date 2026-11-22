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
/// Input for an eight-direction (top-down, screen-axis) movement model: up/down/left/right flags, diagonals by combining
/// two. Facing does not affect the direction. Use <see cref="ForwardMovementPhysxInput"/> for thrust-along-facing controls.
/// </summary>
public class EightDirectionMovementPhysxInput : MovementPhysxInput
{
    /// <summary>Move toward screen up this tick.</summary>
    public bool MoveUp { get; set; }
    /// <summary>Move toward screen down this tick.</summary>
    public bool MoveDown { get; set; }
    /// <summary>Move toward screen left this tick.</summary>
    public bool MoveLeft { get; set; }
    /// <summary>Move toward screen right this tick.</summary>
    public bool MoveRight { get; set; }
    /// <summary>Boost request; its effect is defined by the movement implementation.</summary>
    public bool Turbo { get; set; }
}
