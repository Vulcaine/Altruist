namespace Altruist.Gaming
{
    public interface IWorldPartition
    {
        void AddObject(WorldObjectTypeKey objectType, IObjectMetadata objectMetadata);
        IObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string id);
        HashSet<IObjectMetadata> GetObjectsByType(WorldObjectTypeKey objectType);
        HashSet<IObjectMetadata> GetObjectsByTypeInRoom(WorldObjectTypeKey objectType, string roomId);
    }
}