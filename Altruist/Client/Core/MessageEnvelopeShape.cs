namespace Altruist.Client;

/// <summary>
/// Byte-level parser for the <see cref="MessageEnvelope"/> wire shape — fixarray(3)
/// of <c>[MessageCode (uint), Header, Message]</c>. AOT-safe: never invokes
/// MessagePack on the envelope itself, only on the inner message bytes the consumer
/// already knows the type of.
///
/// <para>Wire format reference (MessagePack spec):</para>
/// <list type="bullet">
///   <item>0x90..0x9f — fixarray, low nibble is element count (we expect 0x93).</item>
///   <item>0xdc — array16 (we tolerate but don't emit).</item>
///   <item>0xdd — array32 (we tolerate but don't emit).</item>
///   <item>uint encoding: 0x00..0x7f literal, 0xcc u8, 0xcd u16, 0xce u32, 0xcf u64.</item>
/// </list>
///
/// <para>Originated as Unity <c>PacketRouter.cs</c>'s hand-rolled parser. Lifted
/// here so the same code is used by every client.</para>
///
/// <para>Used internally by <see cref="ClientPacketDispatcher"/>; call it directly only when
/// routing frames yourself. Only understands MessagePack-encoded envelopes.</para>
/// </summary>
/// <example>
/// <code>
/// uint mc = MessageEnvelopeShape.PeekMessageCode(frame);
/// if (mc == 1001)
/// {
///     var chat = codec.Deserialize&lt;ChatMessagePacket&gt;(MessageEnvelopeShape.ExtractMessage(frame));
/// }
/// </code>
/// </example>
public static class MessageEnvelopeShape
{
    /// <summary>Read the MessageCode (element [0] of the envelope). Returns 0 if
    /// the bytes don't look like a valid envelope.</summary>
    /// <remarks>Accepts fixarray / array16 / array32 headers and positive fixint, uint8, uint16
    /// and uint32 codes; any other encoding yields 0.</remarks>
    /// <param name="data">Envelope bytes.</param>
    /// <returns>The MessageCode, or 0.</returns>
    public static uint PeekMessageCode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return 0;
        byte header = data[0];

        int pos = 1;
        if ((header & 0xf0) == 0x90) { /* fixarray, pos already at 1 */ }
        else if (header == 0xdc) pos = 3;
        else if (header == 0xdd) pos = 5;
        else return 0;

        return ReadUInt(data, pos);
    }

    /// <summary>Slice element [2] (the actual packet bytes) out of the envelope.
    /// Returns an empty array if the envelope is malformed or shorter than 3 elements.
    /// Callers feed the result to their codec's <c>Deserialize&lt;T&gt;</c>.</summary>
    /// <remarks>Requires a 3-element array header (fixarray <c>0x93</c>, or array16 / array32 with a count of 3, the
    /// headers <see cref="PeekMessageCode"/> accepts); returns every byte after the header element, copied into a new array.</remarks>
    /// <param name="data">Envelope bytes.</param>
    /// <returns>The inner message bytes, or an empty array.</returns>
    public static byte[] ExtractMessage(byte[] data)
    {
        if (data is null || data.Length < 3) return Array.Empty<byte>();

        int pos;
        if (data[0] == 0x93) pos = 1;
        else if (data[0] == 0xdc && data.Length >= 3 && ReadBigEndian(data, 1, 2) == 3) pos = 3;
        else if (data[0] == 0xdd && data.Length >= 5 && ReadBigEndian(data, 1, 4) == 3) pos = 5;
        else return Array.Empty<byte>();

        pos = SkipValue(data, pos);
        if (pos < 0) return Array.Empty<byte>();
        pos = SkipValue(data, pos);
        if (pos < 0 || pos >= data.Length) return Array.Empty<byte>();

        var inner = new byte[data.Length - pos];
        Buffer.BlockCopy(data, pos, inner, 0, inner.Length);
        return inner;
    }

    /// <summary>Skip one MessagePack value starting at <paramref name="p"/>;
    /// return the position after it, or -1 if the value is malformed or truncated.</summary>
    /// <remarks>Supports every MessagePack type (nil, bool, ints, floats, str, bin, array, map and ext in all widths).
    /// Never reads past the buffer and never throws for malformed input; nesting deeper than 100 levels is rejected.</remarks>
    /// <param name="d">MessagePack buffer.</param>
    /// <param name="p">Offset of the value to skip.</param>
    /// <returns>Offset just past the value, or -1.</returns>
    public static int SkipValue(byte[] d, int p) => Skip(d, p, 0);

    private const int MaxDepth = 100;

    private static int Skip(byte[] d, int p, int depth)
    {
        if (depth > MaxDepth || p < 0 || p >= d.Length) return -1;
        byte b = d[p];

        if (b <= 0x7f || b >= 0xe0) return p + 1;                       // positive / negative fixint
        if ((b & 0xe0) == 0xa0) return Advance(d, p + 1, b & 0x1f);     // fixstr
        if ((b & 0xf0) == 0x90) return SkipItems(d, p + 1, b & 0x0f, depth);       // fixarray
        if ((b & 0xf0) == 0x80) return SkipItems(d, p + 1, (b & 0x0f) * 2L, depth); // fixmap

        switch (b)
        {
            case 0xc0: case 0xc2: case 0xc3: return p + 1;               // nil, false, true
            case 0xcc: case 0xd0: return Advance(d, p + 1, 1);           // uint8, int8
            case 0xcd: case 0xd1: return Advance(d, p + 1, 2);           // uint16, int16
            case 0xce: case 0xd2: case 0xca: return Advance(d, p + 1, 4); // uint32, int32, float32
            case 0xcf: case 0xd3: case 0xcb: return Advance(d, p + 1, 8); // uint64, int64, float64
            case 0xd9: case 0xc4: return Sized(d, p, 1, 0);              // str8, bin8
            case 0xda: case 0xc5: return Sized(d, p, 2, 0);              // str16, bin16
            case 0xdb: case 0xc6: return Sized(d, p, 4, 0);              // str32, bin32
            case 0xd4: return Advance(d, p + 1, 2);                      // fixext1 (type + 1)
            case 0xd5: return Advance(d, p + 1, 3);                      // fixext2
            case 0xd6: return Advance(d, p + 1, 5);                      // fixext4
            case 0xd7: return Advance(d, p + 1, 9);                      // fixext8
            case 0xd8: return Advance(d, p + 1, 17);                     // fixext16
            case 0xc7: return Sized(d, p, 1, 1);                         // ext8 (+ type byte)
            case 0xc8: return Sized(d, p, 2, 1);                         // ext16
            case 0xc9: return Sized(d, p, 4, 1);                         // ext32
            case 0xdc: return Counted(d, p, 2, 1, depth);                // array16
            case 0xdd: return Counted(d, p, 4, 1, depth);                // array32
            case 0xde: return Counted(d, p, 2, 2, depth);                // map16
            case 0xdf: return Counted(d, p, 4, 2, depth);                // map32
            default: return -1;                                          // 0xc1 (never used)
        }
    }

    // p + n when the buffer holds n more bytes at p, else -1.
    private static int Advance(byte[] d, int p, long n) => p >= 0 && n >= 0 && p + n <= d.Length ? (int)(p + n) : -1;

    // A length-prefixed value: [type][len (lenBytes, big endian)][extra bytes][len bytes].
    private static int Sized(byte[] d, int p, int lenBytes, int extra)
    {
        if (p + 1 + lenBytes > d.Length) return -1;
        return Advance(d, p + 1 + lenBytes, extra + ReadBigEndian(d, p + 1, lenBytes));
    }

    // An array/map: [type][count (countBytes, big endian)] then count * perItem values.
    private static int Counted(byte[] d, int p, int countBytes, int perItem, int depth)
    {
        if (p + 1 + countBytes > d.Length) return -1;
        return SkipItems(d, p + 1 + countBytes, ReadBigEndian(d, p + 1, countBytes) * perItem, depth);
    }

    private static int SkipItems(byte[] d, int p, long count, int depth)
    {
        // Every value takes at least one byte: a count beyond the remaining bytes is truncated input.
        if (count > d.Length - p) return -1;
        for (long i = 0; i < count; i++)
        {
            p = Skip(d, p, depth + 1);
            if (p < 0) return -1;
        }
        return p;
    }

    private static long ReadBigEndian(byte[] d, int p, int bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes; i++) value = (value << 8) | d[p + i];
        return value;
    }

    private static uint ReadUInt(ReadOnlySpan<byte> d, int p)
    {
        if (p >= d.Length) return 0;
        byte b = d[p];
        if (b <= 0x7f) return b;
        if (b == 0xcc && p + 1 < d.Length) return d[p + 1];
        if (b == 0xcd && p + 2 < d.Length) return (uint)((d[p + 1] << 8) | d[p + 2]);
        if (b == 0xce && p + 4 < d.Length)
            return (uint)((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]);
        return 0;
    }
}
