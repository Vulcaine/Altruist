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

namespace Altruist.Codec;

/// <summary>
/// System.Text.Json encoder (UTF-8 bytes, runtime type of the message) using the DI-registered
/// <see cref="JsonSerializerOptions"/>, the same options <see cref="JsonMessageDecoder"/> reads with, so property naming
/// matches in both directions. Registered as the standalone <see cref="IEncoder"/> singleton only when
/// <c>altruist:server:transport:codec:provider</c> is <c>json</c>; normally reached through <see cref="JsonCodec"/>.
/// </summary>
[Service(typeof(IEncoder))]
[ConditionalOnConfig("altruist:server:transport:codec:provider", havingValue: "json")]
public class JsonMessageEncoder : IEncoder
{
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Creates an encoder with the given serializer options.</summary>
    /// <param name="options">Options used for every serialization (the DI-registered ones by default).</param>
    public JsonMessageEncoder(JsonSerializerOptions options)
    {
        _jsonOptions = options;
    }

    /// <inheritdoc/>
    /// <remarks>Serializes by the message's runtime type; a <c>null</c> message yields an empty array.</remarks>
    public byte[] Encode<TPacket>(TPacket message)
    {
        if (message == null)
        {
            return Array.Empty<byte>();
        }
        return JsonSerializer.SerializeToUtf8Bytes(message, message!.GetType(), _jsonOptions);
    }

    /// <inheritdoc/>
    public byte[] Encode(object message, Type type)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, type, _jsonOptions);
    }
}

/// <summary>
/// System.Text.Json decoder using the DI-registered <see cref="JsonSerializerOptions"/>.
/// Registered as the standalone <see cref="IDecoder"/> singleton only when
/// <c>altruist:server:transport:codec:provider</c> is <c>json</c>; normally reached through <see cref="JsonCodec"/>.
/// </summary>
[Service(typeof(IDecoder))]
[ConditionalOnConfig("altruist:server:transport:codec:provider", havingValue: "json")]
public class JsonMessageDecoder : IDecoder
{
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Creates a decoder with the given serializer options.</summary>
    /// <param name="options">Options used for every deserialization.</param>
    public JsonMessageDecoder(JsonSerializerOptions options)
    {
        _jsonOptions = options;
    }

    /// <inheritdoc/>
    public TPacket Decode<TPacket>(byte[] message)
    {
        return JsonSerializer.Deserialize<TPacket>(message, _jsonOptions)!;
    }

    /// <inheritdoc/>
    public TPacket Decode<TPacket>(byte[] message, Type type)
    {
        return (TPacket)JsonSerializer.Deserialize(message, type, _jsonOptions)!;
    }

    /// <inheritdoc/>
    public object Decode(byte[] message, Type type)
    {
        return JsonSerializer.Deserialize(message, type, _jsonOptions)!;
    }
}

/// <summary>
/// JSON wire format (provider name <c>json</c>). Human-readable and easy to consume from browsers; prefer
/// <see cref="Altruist.Codec.MessagePack.MessagePackCodec"/> (<c>messagepack</c>) for smaller, faster binary packets.
/// Select it with <c>altruist:server:transport:codec:provider: json</c>.
/// </summary>
/// <remarks>Does not implement <see cref="IBufferEncoder"/>, so every outbound packet allocates a new array.</remarks>
[CodecProvider("json")]
public class JsonCodec : ICodec
{
    /// <summary>Creates the codec; <paramref name="options"/> (from DI) apply to encoding and decoding.</summary>
    /// <param name="options">Serializer options for the encoder and the decoder.</param>
    public JsonCodec(JsonSerializerOptions options)
    {
        Encoder = new JsonMessageEncoder(options);
        Decoder = new JsonMessageDecoder(options);
    }
    /// <inheritdoc/>
    public IEncoder Encoder { get; }
    /// <inheritdoc/>
    public IDecoder Decoder { get; }
}
