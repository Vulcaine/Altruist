using MessagePack;
using MessagePack.Resolvers;

namespace Altruist.Codec.MessagePack;

public class MessagePackMessageEncoder : IEncoder, IBufferEncoder
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

    public void Encode<TPacket>(System.Buffers.IBufferWriter<byte> writer, TPacket message)
    {
        MessagePackSerializer.Serialize(writer, message, options);
    }
}

/// <summary>
/// Decodes inbound (untrusted) payloads. Every payload is first checked by
/// <see cref="MessagePackStructureGuard"/>: MessagePack's skip/deserialize paths recurse per
/// nesting level, so a ~200 KB payload of nested arrays would otherwise overflow the stack and
/// kill the process (StackOverflowException cannot be caught).
/// </summary>
public class MessagePackMessageDecoder : IDecoder
{
    private MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithResolver(
           CompositeResolver.Create(
               StandardResolverAllowPrivate.Instance,
               TypelessContractlessStandardResolver.Instance
           )
       ).WithSecurity(MessagePackSecurity.UntrustedData);

    public TPacket Decode<TPacket>(byte[] message)
    {
        MessagePackStructureGuard.Validate(message);
        return MessagePackSerializer.Deserialize<TPacket>(message, options);
    }

    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        MessagePackStructureGuard.Validate(message);
        return (TPacket)MessagePackSerializer.Deserialize(type, message, options)!;
    }

    public object Decode(byte[] message, Type type)
    {
        MessagePackStructureGuard.Validate(message);
        return MessagePackSerializer.Deserialize(type, message, options)!;
    }
}

/// <summary>
/// Iterative (non-recursive) structural check of one MessagePack value: nesting depth is capped
/// and every declared array/map length must fit in the remaining bytes. Throws
/// <see cref="MessagePackSerializationException"/> for hostile or truncated input.
/// </summary>
public static class MessagePackStructureGuard
{
    /// <summary>Deepest array/map nesting accepted from the network.</summary>
    public const int MaxDepth = 32;

    public static void Validate(ReadOnlyMemory<byte> data, int maxDepth = MaxDepth)
    {
        if (data.IsEmpty)
            return;
        var reader = new MessagePackReader(data);
        // pending[d] = values still to read at nesting level d; level 0 holds the single root value.
        Span<long> pending = stackalloc long[maxDepth + 1];
        var depth = 0;
        pending[0] = 1;
        try
        {
            while (depth >= 0)
            {
                if (pending[depth] == 0)
                {
                    depth--;
                    continue;
                }
                pending[depth]--;

                long children;
                switch (reader.NextMessagePackType)
                {
                    case MessagePackType.Array:
                        children = reader.ReadArrayHeader();
                        break;
                    case MessagePackType.Map:
                        children = 2L * reader.ReadMapHeader();
                        break;
                    default:
                        reader.Skip(); // scalars, strings, binary and ext: no recursion
                        continue;
                }
                if (children > data.Length - reader.Consumed)
                    throw new MessagePackSerializationException("Declared collection length exceeds the payload.");
                if (children == 0)
                    continue;
                if (depth == maxDepth)
                    throw new MessagePackSerializationException($"Payload nesting exceeds {maxDepth} levels.");
                pending[++depth] = children;
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new MessagePackSerializationException("Truncated payload.", ex);
        }
        catch (OverflowException ex)
        {
            // Some MessagePack versions reject huge map32 headers (count * 2) with an overflow.
            throw new MessagePackSerializationException("Declared collection length exceeds the payload.", ex);
        }
    }
}

[Service(typeof(ICodec))]
[CodecProvider("messagepack")]
public class MessagePackCodec : ICodec
{
    public IEncoder Encoder { get; } = new MessagePackMessageEncoder();
    public IDecoder Decoder { get; } = new MessagePackMessageDecoder();
}
