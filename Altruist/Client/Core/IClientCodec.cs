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
/// </summary>
public interface IClientCodec
{
    /// <summary>Provider name — "messagepack", "json", or a custom string.</summary>
    string Provider { get; }

    /// <summary>Serialize a packet of known type <typeparamref name="T"/>.</summary>
    byte[] Serialize<T>(T value);

    /// <summary>Deserialize bytes into a packet of known type <typeparamref name="T"/>.</summary>
    T? Deserialize<T>(byte[] data);

    /// <summary>Deserialize a slice of bytes into a packet of known type
    /// <typeparamref name="T"/>. Used by <c>PacketRouter</c> to avoid copying the
    /// inner packet bytes out of the framed envelope.</summary>
    T? Deserialize<T>(ReadOnlySpan<byte> data);
}
