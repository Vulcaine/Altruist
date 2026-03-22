namespace Altruist.Gaming;

public interface ISpatialBroadcastService
{
    /// <summary>
    /// Sends a packet to all observers (players) who can see the given entity,
    /// as tracked by the visibility system.
    /// </summary>
    Task SendToObserversAsync(string entityInstanceId, IPacketBase packet);
}
