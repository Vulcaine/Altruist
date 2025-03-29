using System.Text.Json;
using MessagePack;

namespace Altruist.Codec;

public class JsonMessageEncoder : IEncoder
{
    public byte[] Encode<TPacket>(TPacket message)
    {
        if (message == null)
        {
            return Array.Empty<byte>();
        }
        return JsonSerializer.SerializeToUtf8Bytes(message, message!.GetType());
    }

    public byte[] Encode(object message, Type type)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, type);
    }
}


public class MessagePackMessageEncoder : IEncoder
{
    public byte[] Encode<TPacket>(TPacket message)
    {
        return MessagePackSerializer.Serialize(message);
    }

    public byte[] Encode(object message, Type type)
    {
        return MessagePackSerializer.Serialize(type, message);
    }
}


public class JsonMessageDecoder : IDecoder
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TPacket Decode<TPacket>(byte[] message)
    {
        return JsonSerializer.Deserialize<TPacket>(message, _jsonOptions)!;
    }

    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        return (TPacket)JsonSerializer.Deserialize(message, type, _jsonOptions)!;
    }

    public object Decode(byte[] message, Type type)
    {
        return JsonSerializer.Deserialize(message, type, _jsonOptions)!;
    }
}


public class MessagePackMessageDecoder : IDecoder
{
    public TPacket Decode<TPacket>(byte[] message)
    {
        return MessagePackSerializer.Deserialize<TPacket>(message);
    }

    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        return (TPacket)MessagePackSerializer.Deserialize(type, message)!;
    }

    public object Decode(byte[] message, Type type)
    {
        return MessagePackSerializer.Deserialize(type, message)!;
    }
}


public class JsonCodec : ICodec
{
    public IEncoder Encoder { get; } = new JsonMessageEncoder();
    public IDecoder Decoder { get; } = new JsonMessageDecoder();
}

public class MessagePackCodec : ICodec
{
    public IEncoder Encoder { get; } = new MessagePackMessageEncoder();
    public IDecoder Decoder { get; } = new MessagePackMessageDecoder();
}