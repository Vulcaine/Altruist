using MessagePack;
using MessagePack.Resolvers;

namespace Altruist.Codec.MessagePack;

/// <summary>
/// MessagePack encoder (standard resolver allowing private members, with a typeless contractless fallback so
/// types need no <c>[MessagePackObject]</c> attributes). Supports <see cref="IBufferEncoder"/> for allocation-free sends.
/// </summary>
public class MessagePackMessageEncoder : IEncoder, IBufferEncoder
{
    private MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithResolver(
           CompositeResolver.Create(
               StandardResolverAllowPrivate.Instance,
               TypelessContractlessStandardResolver.Instance
           )
       );

    /// <inheritdoc/>
    public byte[] Encode<TPacket>(TPacket message)
    {
        return MessagePackSerializer.Serialize(message, options);
    }

    /// <inheritdoc/>
    public byte[] Encode(object message, Type type)
    {
        return MessagePackSerializer.Serialize(type, message, options);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    /// <exception cref="MessagePackSerializationException">The payload is malformed, truncated or nested too deeply.</exception>
    public TPacket Decode<TPacket>(byte[] message)
    {
        MessagePackStructureGuard.Validate(message);
        return MessagePackSerializer.Deserialize<TPacket>(message, options);
    }

    /// <inheritdoc/>
    /// <exception cref="MessagePackSerializationException">The payload is malformed, truncated or nested too deeply.</exception>
    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        MessagePackStructureGuard.Validate(message);
        return (TPacket)MessagePackSerializer.Deserialize(type, message, options)!;
    }

    /// <inheritdoc/>
    /// <exception cref="MessagePackSerializationException">The payload is malformed, truncated or nested too deeply.</exception>
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

    /// <summary>
    /// Validates one MessagePack value without recursion. Call it before deserializing any untrusted MessagePack
    /// payload yourself; <see cref="MessagePackMessageDecoder"/> already does. An empty payload passes.
    /// </summary>
    /// <param name="data">The encoded value.</param>
    /// <param name="maxDepth">Maximum array/map nesting depth (stack-allocated bookkeeping, keep it small).</param>
    /// <exception cref="MessagePackSerializationException">Nesting exceeds <paramref name="maxDepth"/>, a declared length exceeds the payload, or the payload is truncated.</exception>
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

/// <summary>
/// MessagePack wire format (provider name <c>messagepack</c>): compact binary, hardened against hostile input via
/// <see cref="MessagePackStructureGuard"/> and <c>MessagePackSecurity.UntrustedData</c>. The usual choice for game
/// traffic; use <see cref="Altruist.Codec.JsonCodec"/> (<c>json</c>) when clients need readable text.
/// Select it with <c>altruist:server:transport:codec:provider: messagepack</c>.
/// </summary>
[CodecProvider("messagepack")]
public class MessagePackCodec : ICodec
{
    /// <inheritdoc/>
    public IEncoder Encoder { get; } = new MessagePackMessageEncoder();
    /// <inheritdoc/>
    public IDecoder Decoder { get; } = new MessagePackMessageDecoder();
}
