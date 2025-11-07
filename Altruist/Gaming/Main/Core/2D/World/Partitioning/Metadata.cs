// Altruist.Gaming.TwoD/ObjectMetadata2D.cs
using Altruist.Gaming;
using Altruist.Gaming.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface IObjectMetadata2D : IObjectMetadata
    {
        IntVector2 Position { get; set; }
    }

    /// <summary>2D object metadata.</summary>
    public class ObjectMetadata2D : IObjectMetadata2D
    {
        public WorldObjectTypeKey Type { get; set; } = default!;
        public string InstanceId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public HashSet<string> ReceiverClientIds { get; set; } = new();
        public IntVector2 Position { get; set; }
        public float Rotation { get; set; }
    }
}
