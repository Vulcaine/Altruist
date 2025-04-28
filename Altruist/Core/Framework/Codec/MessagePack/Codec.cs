using MessagePack;
using MessagePack.Resolvers;

namespace Altruist.Codec.MessagePack;

public class MessagePackMessageEncoder : IEncoder
{
    private MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithResolver(
           CompositeResolver.Create(
               StandardResolverAllowPrivate.Instance,
               TypelessContractlessStandardResolver.Instance
           )
       );

    public byte[] Encode<TPacket>(TPacket message)
    {
        return MessagePackSerializer.Serialize(message, options);
    }

    public byte[] Encode(object message, Type type)
    {
        return MessagePackSerializer.Serialize(type, message, options);
    }
}

public class MessagePackMessageDecoder : IDecoder
{
    private MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithResolver(
           CompositeResolver.Create(
               TypelessContractlessStandardResolver.Instance
           )
       );

    public TPacket Decode<TPacket>(byte[] message)
    {
        return MessagePackSerializer.Deserialize<TPacket>(message, options);
    }

    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        return (TPacket)MessagePackSerializer.Deserialize(type, message, options)!;
    }

    public object Decode(byte[] message, Type type)
    {
        return MessagePackSerializer.Deserialize(type, message, options)!;
    }
}


public class MessagePackCodec : ICodec
{
    public IEncoder Encoder { get; } = new MessagePackMessageEncoder();
    public IDecoder Decoder { get; } = new MessagePackMessageDecoder();
}