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

using System.Text.Json.Serialization;
using Altruist.Networking;
using Altruist.UORM;

namespace Altruist.Gaming
{

    public abstract class Vehicle : PlayerEntity
    {
        [VaultColumn]
        [Synced(0)]
        [JsonPropertyName("fuel")]
        public float Fuel { get; set; }

        [VaultColumn]
        [Synced(1)]
        [JsonPropertyName("turboFuel")]
        public float TurboFuel { get; set; }

        [VaultColumn]
        [Synced(2)]
        [JsonPropertyName("maxTurboFuel")]
        public float MaxTurboFuel { get; set; }

        [VaultColumn]
        [Synced(3)]
        [JsonPropertyName("maxTurboSpeed")]
        public float MaxTurboSpeed { get; set; }

        [VaultColumn]
        [Synced(4)]
        [JsonPropertyName("toggleTurbo")]
        public bool ToggleTurbo { get; set; }

        [VaultColumn]
        [Synced(5)]
        [JsonPropertyName("engineQuality")]
        public float EngineQuality { get; set; }

        public Vehicle() { }
        public Vehicle(
        string id,
        int level,
        float[] position,
        float rotation,
        float currentSpeed,
        float maxSpeed,
        float acceleration,
        float maxAcceleration,
        float deceleration,
        float maxDeceleration,
        float rotationSpeed,
        float turboFuel,
        float maxTurboFuel,
        float maxTurboSpeed,
        bool toggleTurbo,
        float engineQuality
    )
        {
            SysId = id;
            Level = level;
            Position = position;
            Rotation = rotation;
            CurrentSpeed = currentSpeed;
            MaxSpeed = maxSpeed;
            Acceleration = acceleration;
            MaxAcceleration = maxAcceleration;
            Deceleration = deceleration;
            MaxDeceleration = maxDeceleration;
            RotationSpeed = rotationSpeed;
            TurboFuel = turboFuel;
            MaxTurboFuel = maxTurboFuel;
            MaxTurboSpeed = maxTurboSpeed;
            ToggleTurbo = toggleTurbo;
            EngineQuality = engineQuality;
        }


        public void UpdateSpeed()
        {
            if (CurrentSpeed == 0) return;

            if (CurrentSpeed > 0)
            {
                CurrentSpeed -= Deceleration;
                if (CurrentSpeed < 0)
                {
                    CurrentSpeed = 0;
                }
            }
        }
    }



    public class Spaceship : Vehicle
    {
        [VaultColumn]
        [Synced(0)]
        [JsonPropertyName("shootSpeed")]
        public float ShootSpeed { get; set; }

        protected Spaceship()
        {
        }

        protected Spaceship(
        string id,
        int level,
        float[] position,
        float rotation,
        float currentSpeed,
        float maxSpeed,
        float acceleration,
        float maxAcceleration,
        float deceleration,
        float maxDeceleration,
        float rotationSpeed,
        float turboFuel,
        float maxTurboFuel,
        float maxTurboSpeed,
        bool toggleTurbo,
        float engineQuality,
        float shootSpeed
    ) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, maxTurboSpeed, toggleTurbo, engineQuality)
        {
            ShootSpeed = shootSpeed;
        }

    }

    public abstract class Car : Vehicle
    {
        protected Car()
        {
        }

        protected Car(
        string id,
        int level,
        float[] position,
        float rotation,
        float currentSpeed,
        float maxSpeed,
        float acceleration,
        float maxAcceleration,
        float deceleration,
        float maxDeceleration,
        float rotationSpeed,
        float turboFuel,
        float maxTurboFuel,
        float maxTurboSpeed,
        bool toggleTurbo,
        float engineQuality
    ) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, maxTurboSpeed, toggleTurbo, engineQuality)
        {
        }

    }
}