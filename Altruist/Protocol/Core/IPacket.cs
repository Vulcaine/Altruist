namespace Altruist;

/// <summary>
/// Marker interface for any model that is sent over the wire. Carries no members.
/// </summary>
/// <remarks>
/// Concrete packets should implement <see cref="IPacketBase"/>, which extends this
/// interface with the <see cref="IPacketBase.MessageCode"/> used for routing; use
/// <see cref="IPacket"/> alone only as a generic constraint or for payloads that are
/// always nested inside another packet.
/// </remarks>
public interface IPacket : ITypedModel
{
}
