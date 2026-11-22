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

    /// <summary>
    /// A ready-made player model for simple 2D games: persisted as a vault model (<c>[VaultColumn]</c>),
    /// serialized with MessagePack/JSON, and delta-synced (<c>[Synced]</c>) through
    /// <see cref="IAltruistRouter.Synchronize"/>. It can carry a Box2D body (<see cref="AttachBody"/>) whose
    /// position and angle <see cref="Update"/> copies back.
    /// <para>
    /// When to use: prototypes and modules that take a <see cref="PlayerEntity"/> (inventory item hooks,
    /// inventory portal). Derive from it to add fields (override <see cref="InitDefaults"/> for their defaults).
    /// For world objects in the 2D/3D world organizers use their world-object types; for match rooms keep
    /// player state in the room's simulation and the game's <c>TPlayer</c>.
    /// </para>
    /// </summary>
    public class PlayerEntity : VaultModel, ISynchronizedEntity
    {
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider calling the parameterless constructor.

        /// <summary>Persistent id (a new GUID by default; JSON <c>id</c>).</summary>
        [Key(0)]
        [JsonPropertyName("id")]
        [VaultColumn]

        public override string StorageId { get; set; }

        /// <summary>The connection that controls the player ("" when none). Always synced.</summary>
        [Key(1)]
        [Synced(0, SyncAlways: true)]
        [JsonPropertyName("clientId")]
        [VaultColumn]
        public string ClientId { get; set; }

        /// <summary>Display name (default "Player"). Always synced.</summary>
        [Key(2)]
        [Synced(1, SyncAlways: true)]
        [JsonPropertyName("name")]
        [VaultColumn]
        public string Name { get; set; }

        /// <summary>The model's type name (defaults to the runtime class name). Always synced.</summary>
        [Key(3)]
        [Synced(2, SyncAlways: true)]
        [JsonPropertyName("type")]
        [VaultColumn]
        public override string Type { get; set; }

        /// <summary>Player level (default 1).</summary>
        [Key(4)]
        [Synced(3)]
        [JsonPropertyName("level")]
        [VaultColumn]
        public int Level { get; set; }

        /// <summary>Position as <c>[x, y]</c> in world units (default origin).</summary>
        [Key(5)]
        [Synced(4)]
        [JsonPropertyName("position")]
        [VaultColumn]
        public float[] Position { get; set; }

        /// <summary>Facing angle in radians (Box2D convention; copied from the body by <see cref="Update"/>).</summary>
        [Key(6)]
        [Synced(5)]
        [JsonPropertyName("rotation")]
        [VaultColumn]
        public float Rotation { get; set; }

        /// <summary>Current speed (world units per second).</summary>
        [Key(7)]
        [Synced(6)]
        [JsonPropertyName("currentSpeed")]
        [VaultColumn]
        public float CurrentSpeed { get; set; }

        /// <summary>Turn rate (radians per second).</summary>
        [Key(8)]
        [JsonPropertyName("rotationSpeed")]
        [VaultColumn]
        [Synced(7)]
        public float RotationSpeed { get; set; }

        /// <summary>Speed cap (world units per second).</summary>
        [Key(9)]
        [JsonPropertyName("maxSpeed")]
        [Synced(5)]
        [VaultColumn]
        public float MaxSpeed { get; set; }

        /// <summary>Current acceleration.</summary>
        [Key(10)]
        [JsonPropertyName("acceleration")]
        [Synced(8)]
        [VaultColumn]
        public float Acceleration { get; set; }

        /// <summary>Current deceleration.</summary>
        [Key(11)]
        [JsonPropertyName("deceleration")]
        [Synced(9)]
        [VaultColumn]
        public float Deceleration { get; set; }

        /// <summary>Deceleration cap.</summary>
        [Key(12)]
        [JsonPropertyName("maxDeceleration")]
        [Synced(10)]
        [VaultColumn]
        public float MaxDeceleration { get; set; }

        /// <summary>Acceleration cap.</summary>
        [Key(13)]
        [JsonPropertyName("maxAcceleration")]
        [Synced(11)]
        [VaultColumn]
        public float MaxAcceleration { get; set; }

        /// <summary>Index of the world the player is in (<see cref="IWorldIndex.Index"/>). Not synced.</summary>
        [Key(14)]
        [JsonPropertyName("worldIndex")]
        [VaultColumn]
        public int WorldIndex { get; set; }

        /// <summary>The player is moving. Not synced.</summary>
        [Key(15)]
        [JsonPropertyName("moving")]
        [VaultColumn]
        public bool Moving { get; set; }

        /// <summary>Last-modified time (UTC) of the stored row.</summary>
        [Key(16)]
        [VaultColumn]
        public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>Size of the player's box in world units (default 1 x 1).</summary>
        [Key(17)]
        [VaultColumn]
        public Vector2 Size { get; set; }

        /// <summary>The attached Box2D body, or null. Not persisted or serialized.</summary>
        [JsonIgnore]
        [IgnoreMember]
        [VaultIgnore]
        public Body? PhysxBody { get; private set; }

        /// <summary>Sets every field to its default; called by both constructors. Override to default your own fields (call the base).</summary>
        protected virtual void InitDefaults()
        {
            Type = GetType().Name;
            StorageId = Guid.NewGuid().ToString();
            ClientId = "";
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

        /// <summary>A player with default values and a new GUID <see cref="StorageId"/>.</summary>
        public PlayerEntity()

        {
            InitDefaults();
        }

        /// <summary>A player with default values and the given <see cref="StorageId"/>.</summary>
        /// <param name="id">The persistent id.</param>
        public PlayerEntity(string id)
        {
            InitDefaults();
            StorageId = id;
        }

        /// <summary>Attaches a Box2D body; <see cref="Update"/> then follows it.</summary>
        public void AttachBody(Body body) => PhysxBody = body;

        /// <summary>Destroys the attached body in its world (if any) and detaches it.</summary>
        public virtual void DetachBody()
        {
            PhysxBody?.World.DestroyBody(PhysxBody);
            PhysxBody = null;
        }

        /// <summary>Copies the attached body's position and angle into <see cref="Position"/> and <see cref="Rotation"/> when the position changed; no body: nothing changes.</summary>
        /// <returns>This player.</returns>
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
