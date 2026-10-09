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

namespace Altruist;

/// <summary>
/// A wire format for realtime packets: a matched <see cref="IEncoder"/>/<see cref="IDecoder"/> pair.
/// </summary>
/// <remarks>
/// Built-in providers are <c>json</c> (<see cref="Altruist.Codec.JsonCodec"/>) and <c>messagepack</c>
/// (<see cref="Altruist.Codec.MessagePack.MessagePackCodec"/>). Select one with
/// <c>altruist:server:transport:codec:provider</c> (or per transport with
/// <c>altruist:server:transport:&lt;mode&gt;:codec:provider</c>); look codecs up through <see cref="ICodecResolver"/>.
/// To add a custom format implement this interface and mark it with <see cref="CodecProviderAttribute"/>.
/// For stream transports that need packet boundaries also implement <see cref="IFramedCodec"/>.
/// </remarks>
/// <example>
/// <code>
/// [Service(typeof(ICodec))]
/// [CodecProvider("protobuf")]
/// public class ProtobufCodec : ICodec
/// {
///     public IEncoder Encoder { get; } = new ProtobufEncoder();
///     public IDecoder Decoder { get; } = new ProtobufDecoder();
/// }
/// // config.yml: altruist:server:transport:codec:provider: protobuf
/// </code>
/// </example>
public interface ICodec
{
    /// <summary>Serializes outbound packets to bytes.</summary>
    IEncoder Encoder { get; }
    /// <summary>Deserializes inbound bytes to packets. Input is untrusted network data, so decoders should reject hostile payloads.</summary>
    IDecoder Decoder { get; }
}

/// <summary>Serializes packets to the codec's wire format. Implementations should be thread-safe; one instance serves all connections.</summary>
public interface IEncoder
{
    /// <summary>Serializes <paramref name="message"/> to a new byte array.</summary>
    /// <typeparam name="TPacket">Static type of the packet; implementations may serialize by the runtime type instead (the JSON encoder does).</typeparam>
    /// <param name="message">The packet to serialize.</param>
    /// <returns>The encoded bytes.</returns>
    byte[] Encode<TPacket>(TPacket message);
    /// <summary>Serializes <paramref name="message"/> as <paramref name="type"/>; use when the packet type is only known at runtime.</summary>
    /// <param name="message">The packet to serialize.</param>
    /// <param name="type">The type to serialize the packet as.</param>
    /// <returns>The encoded bytes.</returns>
    byte[] Encode(object message, Type type);
}

/// <summary>
/// Optional encoder capability: writes into a caller-owned (reusable) buffer instead of
/// allocating a byte[] per message. Same bytes as <see cref="IEncoder.Encode{TPacket}(TPacket)"/>.
/// </summary>
/// <remarks>
/// Implement it on your <see cref="IEncoder"/> when the format can stream into a writer; the outbound send queue
/// detects it (<c>Encoder is IBufferEncoder</c>) and reuses a per-client buffer, otherwise it falls back to
/// <see cref="IEncoder.Encode{TPacket}(TPacket)"/>. The MessagePack encoder implements it; the JSON encoder does not.
/// </remarks>
public interface IBufferEncoder
{
    /// <summary>Serializes <paramref name="message"/> directly into <paramref name="writer"/>.</summary>
    /// <typeparam name="TPacket">Static type of the packet.</typeparam>
    /// <param name="writer">Caller-owned buffer to append the encoded bytes to.</param>
    /// <param name="message">The packet to serialize.</param>
    void Encode<TPacket>(System.Buffers.IBufferWriter<byte> writer, TPacket message);
}

/// <summary>Deserializes inbound packet bytes. Implementations should be thread-safe and must treat input as untrusted.</summary>
public interface IDecoder
{
    /// <summary>Deserializes <paramref name="message"/> as <paramref name="type"/>, when the target type is only known at runtime.</summary>
    /// <param name="message">Encoded packet bytes.</param>
    /// <param name="type">Target type.</param>
    /// <returns>The decoded object.</returns>
    object Decode(byte[] message, Type type);
    /// <summary>Deserializes <paramref name="message"/> as <typeparamref name="TPacket"/>.</summary>
    /// <typeparam name="TPacket">Target type.</typeparam>
    /// <param name="message">Encoded packet bytes.</param>
    /// <returns>The decoded packet.</returns>
    TPacket Decode<TPacket>(byte[] message);
    /// <summary>Deserializes <paramref name="message"/> as the runtime <paramref name="type"/> and casts the result to <typeparamref name="TPacket"/> (e.g. a concrete packet read through a base interface).</summary>
    /// <typeparam name="TPacket">Type to cast the result to; must be assignable from <paramref name="type"/>.</typeparam>
    /// <param name="message">Encoded packet bytes.</param>
    /// <param name="type">Concrete type to deserialize.</param>
    /// <returns>The decoded packet.</returns>
    TPacket Decode<TPacket>(byte[] message, Type type);
}

/// <summary>
/// Optional extension for codecs that need stream-level packet framing (e.g. raw TCP binary protocols).
/// If a codec implements this interface, the ConnectionManager will buffer incoming bytes
/// and use the framer to extract complete packets before decoding.
/// Codecs that do NOT implement this (MessagePack, JSON, WebSocket) are completely unaffected.
/// </summary>
/// <remarks>
/// Only needed for raw byte-stream transports where one receive can hold partial or multiple packets; for
/// message-oriented transports (WebSocket) implement plain <see cref="ICodec"/>. Framing is decided by the
/// global default codec (<see cref="ICodecResolver.Resolve"/> with no transport mode).
/// </remarks>
public interface IFramedCodec : ICodec
{
    /// <summary>The framer that splits the receive stream into individual packets.</summary>
    IPacketFramer Framer { get; }
}

/// <summary>
/// Extracts individual packet byte arrays from a raw TCP byte stream.
/// The framer is responsible for knowing packet boundaries (e.g. via opcode + size lookup).
/// </summary>
public interface IPacketFramer
{
    /// <summary>
    /// Attempts to extract the next complete packet from the front of the buffer.
    /// On success: returns the packet bytes and advances consumed past them.
    /// On failure (not enough data): returns null, consumed is set to 0.
    /// </summary>
    /// <param name="buffer">The accumulated receive buffer.</param>
    /// <param name="consumed">Number of bytes consumed from the front of the buffer.</param>
    byte[]? TryFrame(ReadOnlySpan<byte> buffer, out int consumed);
}
