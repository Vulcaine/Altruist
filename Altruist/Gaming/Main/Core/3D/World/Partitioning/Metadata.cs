// Altruist.Gaming.ThreeD/ObjectMetadata3D.cs
using Altruist.Gaming;
using Altruist.Gaming.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{

    public interface IObjectMetadata3D : IObjectMetadata
    {
        IntVector3 Position { get; set; }
    }

    /// <summary>3D object metadata.</summary>
    public class ObjectMetadata3D : IObjectMetadata3D
    {
        public WorldObjectTypeKey Type { get; set; } = default!;
        public string InstanceId { get; set; } = "";
        public string RoomId { get; set; } = "";
        public HashSet<string> ReceiverClientIds { get; set; } = new();
        public IntVector3 Position { get; set; }
        public float Rotation { get; set; }
    }
}
