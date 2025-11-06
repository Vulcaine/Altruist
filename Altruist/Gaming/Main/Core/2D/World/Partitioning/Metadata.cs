using Altruist.Gaming.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{

    /// <summary>
    /// Represents metadata about a world object, including its type, position,
    /// instance identifier, associated room, and nearby/connected clients.
    /// </summary>
    public class ObjectMetadata2D
    {
        /// <summary>
        /// The type of the world object (e.g., player, NPC, item).
        /// </summary>
        public WorldObjectTypeKey Type { get; set; } = default!;

        /// <summary>
        /// A unique identifier for this specific instance of the object.
        /// </summary>
        public string InstanceId { get; set; } = "";

        /// <summary>
        /// The identifier of the room this object belongs to.
        /// </summary>
        public string RoomId { get; set; } = "";

        /// <summary>
        /// A set of client IDs that have received this object.
        /// 
        /// This is used to:
        /// - Track which connected clients are aware of this object,
        /// - Determine which clients should be notified when the object is removed,
        /// - Track client proximity or visibility to this object.
        /// </summary>
        public HashSet<string> ReceiverClientIds { get; set; } = new();

        /// <summary>
        /// The (X, Y) position of the object in the world/grid.
        /// </summary>
        public IntVector2 Position { get; set; }

        public float Rotation { get; set; }
    }
}