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
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using Altruist.Networking;
using Altruist.UORM;

using Box2DSharp.Dynamics;

using MessagePack;

namespace Altruist.Gaming
{

    public class PlayerEntity : VaultModel, ISynchronizedEntity
    {
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider calling the parameterless constructor.

        [Key(0)]
        [JsonPropertyName("id")]
        [VaultColumn]

        public override string StorageId { get; set; }

        [Key(1)]
        [Synced(0, SyncAlways: true)]
        [JsonPropertyName("connectionId")]
        [VaultColumn]
        public string ConnectionId { get; set; }

        [Key(2)]
        [Synced(1, SyncAlways: true)]
        [JsonPropertyName("name")]
        [VaultColumn]
        public string Name { get; set; }

        [Key(3)]
        [Synced(2, SyncAlways: true)]
        [JsonPropertyName("type")]
        [VaultColumn]
        public override string Type { get; set; }

        [Key(4)]
        [Synced(3)]
        [JsonPropertyName("level")]
        [VaultColumn]
        public int Level { get; set; }

        [Key(5)]
        [Synced(4)]
        [JsonPropertyName("position")]
        [VaultColumn]
        public float[] Position { get; set; }

        [Key(6)]
        [Synced(5)]
        [JsonPropertyName("rotation")]
        [VaultColumn]
        public float Rotation { get; set; }

        [Key(7)]
        [Synced(6)]
        [JsonPropertyName("currentSpeed")]
        [VaultColumn]
        public float CurrentSpeed { get; set; }

        [Key(8)]
        [JsonPropertyName("rotationSpeed")]
        [VaultColumn]
        [Synced(7)]
        public float RotationSpeed { get; set; }

        [Key(9)]
        [JsonPropertyName("maxSpeed")]
        [Synced(5)]
        [VaultColumn]
        public float MaxSpeed { get; set; }

        [Key(10)]
        [JsonPropertyName("acceleration")]
        [Synced(8)]
        [VaultColumn]
        public float Acceleration { get; set; }

        [Key(11)]
        [JsonPropertyName("deceleration")]
        [Synced(9)]
        [VaultColumn]
        public float Deceleration { get; set; }

        [Key(12)]
        [JsonPropertyName("maxDeceleration")]
        [Synced(10)]
        [VaultColumn]
        public float MaxDeceleration { get; set; }

        [Key(13)]
        [JsonPropertyName("maxAcceleration")]
        [Synced(11)]
        [VaultColumn]
        public float MaxAcceleration { get; set; }

        [Key(14)]
        [JsonPropertyName("worldIndex")]
        [VaultColumn]
        public int WorldIndex { get; set; }

        [Key(15)]
        [JsonPropertyName("moving")]
        [VaultColumn]
        public bool Moving { get; set; }

        [Key(16)]
        [VaultColumn]
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Key(17)]
        [VaultColumn]
        public Vector2 Size { get; set; }

        [JsonIgnore]
        [IgnoreMember]
        [VaultIgnore]
        public Body? PhysxBody { get; private set; }

        protected virtual void InitDefaults()
        {
            Type = GetType().Name;
            StorageId = Guid.NewGuid().ToString();
            ConnectionId = "";
            Name = "Player";
            Level = 1;
            Position = [0, 0];
            Rotation = 0;
            CurrentSpeed = 0;
            RotationSpeed = 0;
            MaxSpeed = 0;
            Acceleration = 0;
            Deceleration = 0;
            MaxDeceleration = 0;
            MaxAcceleration = 0;
            Size = new Vector2(1, 1);
        }

        public PlayerEntity()

        {
            InitDefaults();
        }

        public PlayerEntity(string id)
        {
            InitDefaults();
            StorageId = id;
        }

        public void AttachBody(Body body) => PhysxBody = body;

        public virtual void DetachBody()
        {
            PhysxBody?.World.DestroyBody(PhysxBody);
            PhysxBody = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual PlayerEntity Update()
        {
            var position = PhysxBody?.GetPosition();
            if (PhysxBody != null && (Position[0] != position?.X || Position[1] != position?.Y))
            {
                Position[0] = position?.X! ?? Position[0];
                Position[1] = position?.Y! ?? Position[1];
                Rotation = PhysxBody.GetAngle();
            }

            return this;
        }

        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // public virtual Body CalculatePhysxBody(World world)
        // {
        //     if (PhysxBody != null) return PhysxBody;

        //     // Define the body
        //     var bodyDef = new BodyDef
        //     {
        //         BodyType = BodyType.DynamicBody,
        //         Position = new Vector2(Position[0], Position[1]),
        //         Angle = Rotation,
        //         FixedRotation = true,
        //         LinearDamping = 1f
        //     };

        //     // Create the body
        //     var body = world.CreateBody(bodyDef);

        //     // Define the shape
        //     var shape = new PolygonShape();
        //     shape.SetAsBox(Size.X * 0.5f, Size.Y * 0.5f); // Box2D uses half-widths

        //     // Define the fixture
        //     var fixtureDef = new FixtureDef
        //     {
        //         Shape = shape,
        //         Density = 1f,
        //         Friction = 0.2f
        //     };

        //     // Attach the shape to the body
        //     body.CreateFixture(fixtureDef);
        //     AttachBody(body);
        //     return body;
        // }

    }
}
