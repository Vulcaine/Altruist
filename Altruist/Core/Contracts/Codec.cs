namespace Altruist;

public interface ICodec
{
    IEncoder Encoder { get; }
    IDecoder Decoder { get; }
}

public interface IEncoder
{
    byte[] Encode<TPacket>(TPacket message);
    byte[] Encode(object message, Type type);
}

public interface IDecoder
{
    object Decode(byte[] message, Type type);
    TPacket Decode<TPacket>(byte[] message);
    TPacket Decode<TPacket>(byte[] message, Type type);
}
