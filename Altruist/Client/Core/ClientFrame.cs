using System.Text;

namespace Altruist.Client;

/// <summary>
/// The client-to-server frame body every Altruist transport speaks for binary codecs:
/// <c>[u8 gateLength][gate UTF-8][payload]</c>. The server reads the first byte as the gate length only when it is
/// 1..127 (<see cref="MaxGateBytes"/>), so longer or empty gate names would be misread.
/// </summary>
/// <remarks>
/// Used by the TCP (inside its length prefix), UDP (one datagram) and WebSocket (one binary message) clients. Twin of
/// <c>clientFrame</c> in <c>@altruist/net</c>.
/// </remarks>
internal static class ClientFrame
{
    /// <summary>Longest gate name in UTF-8 bytes the server's framing accepts.</summary>
    public const int MaxGateBytes = 127;

    /// <summary>The UTF-8 bytes of <paramref name="gate"/>, checked against the 1..<see cref="MaxGateBytes"/> limit.</summary>
    /// <exception cref="ArgumentException">The gate name is empty or longer than <see cref="MaxGateBytes"/> UTF-8 bytes.</exception>
    public static byte[] GateBytes(string gate)
    {
        var bytes = Encoding.UTF8.GetBytes(gate ?? "");
        if (bytes.Length < 1 || bytes.Length > MaxGateBytes)
            throw new ArgumentException($"Gate name must be 1..{MaxGateBytes} UTF-8 bytes; '{gate}' is {bytes.Length}.", nameof(gate));
        return bytes;
    }

    /// <summary>
    /// Writes <c>[u8 gateLength][gate][payload]</c> into a new array, leaving <paramref name="headroom"/> bytes free at
    /// the front (the TCP client writes its length prefix there).
    /// </summary>
    public static byte[] Build(byte[] gateBytes, byte[] payload, int headroom = 0)
    {
        var frame = new byte[headroom + 1 + gateBytes.Length + payload.Length];
        frame[headroom] = (byte)gateBytes.Length;
        Buffer.BlockCopy(gateBytes, 0, frame, headroom + 1, gateBytes.Length);
        Buffer.BlockCopy(payload, 0, frame, headroom + 1 + gateBytes.Length, payload.Length);
        return frame;
    }
}
