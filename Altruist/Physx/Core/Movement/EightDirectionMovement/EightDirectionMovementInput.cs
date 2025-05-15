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

namespace Altruist.Physx;

public class EightDirectionMovementPhysxInput : IMovementStats, IDirectionalInput2D
{
    public float CurrentSpeed { get; set; }
    public float MaxSpeed { get; set; }
    public float Acceleration { get; set; }
    public float Deceleration { get; set; }
    public float DeltaTime { get; set; } = 1.0f;

    public int MoveLeftRight { get; set; }
    public int MoveUpDown { get; set; }

    public bool Turbo { get; set; }
}
