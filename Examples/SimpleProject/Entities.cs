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


using Altruist.Gaming;

namespace SimpleGame.Entities;

public class SimpleSpaceship : Spaceship
{
    protected override void InitDefaults()
    {
        base.InitDefaults();
        MaxSpeed = 20f;
        MaxTurboSpeed = 10f;
        RotationSpeed = 0.01f;
        Acceleration = 2f;
        MaxAcceleration = 5f;
        Deceleration = 5f;
        MaxDeceleration = 5f;
        TurboFuel = 100f;
        MaxTurboFuel = 100f;
        ToggleTurbo = false;
        EngineQuality = 1f;
        ShootSpeed = 5f;
    }
}
