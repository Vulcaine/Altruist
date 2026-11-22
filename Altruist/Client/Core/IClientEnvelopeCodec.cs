namespace Altruist.Client;

/// <summary>
/// A codec whose server frames are not MessagePack: it reads the server's <see cref="MessageEnvelope"/> in its own wire
/// format. <see cref="ClientPacketDispatcher"/> uses it for codecs that implement it and parses every other codec's
/// frames as MessagePack envelopes (<see cref="MessageEnvelopeShape"/>).
/// </summary>
/// <remarks>
/// <see cref="JsonClientCodec"/> implements it (the server's JSON codec sends <c>{"messageCode", "header", "message"}</c>).
/// Implement it on a custom codec whose server counterpart encodes the envelope in another format.
/// </remarks>
public interface IClientEnvelopeCodec : IClientCodec
{
    /// <summary>
    /// Reads the message code and the encoded inner packet (bytes this codec's <c>Deserialize&lt;T&gt;</c> accepts) of
    /// one server frame. Returns false for frames that are not an envelope; never throws for malformed input.
    /// </summary>
    /// <param name="frame">One complete server frame.</param>
    /// <param name="messageCode">The envelope's message code (0 when false is returned).</param>
    /// <param name="message">The encoded inner packet (empty when false is returned).</param>
    bool TryReadEnvelope(byte[] frame, out uint messageCode, out byte[] message);
}
