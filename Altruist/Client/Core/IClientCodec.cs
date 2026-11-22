namespace Altruist.Client;

/// <summary>
/// Pluggable codec contract. Default impl is <see cref="MessagePackClientCodec"/>;
/// JSON is offered for browser/WebSocket scenarios. Consumers can ship their own.
///
/// <para>The contract is split deliberately: <see cref="Serialize{T}"/> is generic
/// (AOT-safe — formatter for T is resolved at compile time) and the deserialize
/// overloads also take a known T. There is no <c>Serialize(object)</c> on the hot
/// path: that would force reflection / typeless resolution which doesn't work on
/// stripped runtimes.</para>
///
/// <para>Implementations are discovered by <see cref="ClientCodecResolver"/> through DI
/// (<c>[Service(typeof(IClientCodec))]</c>) and selected per transport by
/// <see cref="Provider"/> name from <c>altruist:client:transport:&lt;tcp|udp|ws&gt;:codec:provider</c>.
/// Registering a codec whose <see cref="Provider"/> equals a built-in one replaces it.
/// Codecs are shared across transports and threads, so implementations must be thread-safe.</para>
/// </summary>
/// <example>
/// <code>
/// [Service(typeof(IClientCodec))]
/// public sealed class MyCodec : IClientCodec
/// {
///     public string Provider =&gt; "mycodec";   // config: codec: { provider: mycodec }
///     public byte[] Serialize&lt;T&gt;(T value) =&gt; ...;
///     public T? Deserialize&lt;T&gt;(byte[] data) =&gt; ...;
///     public T? Deserialize&lt;T&gt;(ReadOnlySpan&lt;byte&gt; data) =&gt; ...;
/// }
/// </code>
/// </example>
public interface IClientCodec
{
    /// <summary>Provider name — "messagepack", "json", or a custom string. Matched
    /// case-insensitively by <see cref="ClientCodecResolver.Resolve"/>.</summary>
    string Provider { get; }

    /// <summary>Serialize a packet of known type <typeparamref name="T"/>. The built-in codecs
    /// return an empty array for <c>null</c>.</summary>
    /// <typeparam name="T">Static packet type; its formatter / contract is used.</typeparam>
    /// <param name="value">Packet to encode.</param>
    /// <returns>Encoded bytes.</returns>
    byte[] Serialize<T>(T value);

    /// <summary>Deserialize bytes into a packet of known type <typeparamref name="T"/>. The
    /// built-in codecs return <c>default</c> for <c>null</c> / empty input. This is the overload
    /// <see cref="ClientPacketDispatcher"/> calls.</summary>
    /// <typeparam name="T">Concrete packet type to produce.</typeparam>
    /// <param name="data">Encoded packet bytes.</param>
    /// <returns>The decoded packet, or <c>default</c>.</returns>
    T? Deserialize<T>(byte[] data);

    /// <summary>Deserialize a slice of bytes into a packet of known type
    /// <typeparamref name="T"/>, for callers that hold the inner packet as a span of a larger
    /// buffer. (The built-in <see cref="MessagePackClientCodec"/> still copies the span
    /// internally.)</summary>
    /// <typeparam name="T">Concrete packet type to produce.</typeparam>
    /// <param name="data">Encoded packet bytes.</param>
    /// <returns>The decoded packet, or <c>default</c> for empty input.</returns>
    T? Deserialize<T>(ReadOnlySpan<byte> data);
}
