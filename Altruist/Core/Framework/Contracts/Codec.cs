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

/// <summary>
/// Optional extension for codecs that need stream-level packet framing (e.g. raw TCP binary protocols).
/// If a codec implements this interface, the ConnectionManager will buffer incoming bytes
/// and use the framer to extract complete packets before decoding.
/// Codecs that do NOT implement this (MessagePack, JSON, WebSocket) are completely unaffected.
/// </summary>
public interface IFramedCodec : ICodec
{
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
