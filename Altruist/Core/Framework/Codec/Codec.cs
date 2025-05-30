/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;

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




public class JsonCodec : ICodec
{
    public IEncoder Encoder { get; } = new JsonMessageEncoder();
    public IDecoder Decoder { get; } = new JsonMessageDecoder();
}
