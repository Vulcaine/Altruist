// Altruist.Gaming/IObjectMetadata.cs
namespace Altruist.Gaming
{
    public interface IObjectMetadata
    {
        WorldObjectTypeKey Type { get; set; }
        string InstanceId { get; set; }
        string RoomId { get; set; }
        HashSet<string> ReceiverClientIds { get; set; }
        float Rotation { get; set; }
    }

}
