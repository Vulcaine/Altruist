namespace Altruist;

/// <summary>
/// Root marker interface for every framework model that travels through Altruist's
/// serialization layers (wire packets via <see cref="IPacket"/>, and on the server also
/// stored models). Carries no members; it only lets generic code constrain on "an
/// Altruist model".
/// </summary>
/// <remarks>
/// Do not implement this directly for network messages: implement <see cref="IPacketBase"/>
/// (which adds the routing <see cref="IPacketBase.MessageCode"/>) instead.
/// </remarks>
public interface ITypedModel
{
}
