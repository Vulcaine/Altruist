using Altruist.Networking.Codec.MessagePack;
using MessagePack;

namespace Altruist;

[MessagePackFormatter(typeof(PacketBaseFormatter))]
public interface IPacketBase : IPacket
{
    uint MessageCode { get; set; }
}
