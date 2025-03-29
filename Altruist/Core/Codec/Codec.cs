using System.Text.Json;
using Altruist;
using MessagePack;

public class JsonMessageEncoder : IMessageEncoder
{
    public byte[] Encode<TPacket>(TPacket message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message);
    }

    public byte[] Encode(object message, Type type)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, type);
    }
}


public class MessagePackMessageEncoder : IMessageEncoder
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


public class JsonMessageDecoder : IMessageDecoder
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


public class MessagePackMessageDecoder : IMessageDecoder
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
