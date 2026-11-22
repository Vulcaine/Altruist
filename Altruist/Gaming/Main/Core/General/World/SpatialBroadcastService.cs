namespace Altruist.Gaming;

/// <summary>
/// Visibility-aware broadcasting for persistent worlds: sends to the players that currently see an
/// entity (the observer sets maintained by the visibility tracker). Dimension-specific variants:
/// <c>ISpatialBroadcastService2D</c> / <c>ISpatialBroadcastService3D</c>. For match rooms, broadcast
/// through the room instead; to reach every client use the plain router.
/// </summary>
public interface ISpatialBroadcastService
{
    /// <summary>
    /// Sends a packet to all observers (players) who can see the given entity,
    /// as tracked by the visibility system.
    /// </summary>
    Task SendToObserversAsync(string entityInstanceId, IPacketBase packet);
}
