namespace Altruist;

public interface IMessageEncoder
{
    byte[] Encode<TPacket>(TPacket message);
    byte[] Encode(object message, Type type);
}

public interface IMessageDecoder
{
    object Decode(byte[] message, Type type);
    TPacket Decode<TPacket>(byte[] message);
    TPacket Decode<TPacket>(byte[] message, Type type);
}
