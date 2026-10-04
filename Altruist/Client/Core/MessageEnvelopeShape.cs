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
/// </summary>
public static class MessageEnvelopeShape
{
    /// <summary>Read the MessageCode (element [0] of the envelope). Returns 0 if
    /// the bytes don't look like a valid envelope.</summary>
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
    public static byte[] ExtractMessage(byte[] data)
    {
        if (data.Length < 3) return Array.Empty<byte>();
        if (data[0] != 0x93) return Array.Empty<byte>();

        int pos = 1;
        pos = SkipValue(data, pos);
        if (pos < 0) return Array.Empty<byte>();
        pos = SkipValue(data, pos);
        if (pos < 0 || pos >= data.Length) return Array.Empty<byte>();

        var inner = new byte[data.Length - pos];
        Buffer.BlockCopy(data, pos, inner, 0, inner.Length);
        return inner;
    }

    /// <summary>Skip one MessagePack value starting at <paramref name="p"/>;
    /// return the position after it, or -1 if the value type is unknown / data
    /// is truncated.</summary>
    public static int SkipValue(byte[] d, int p)
    {
        if (p >= d.Length) return -1;
        byte b = d[p];

        // Positive fixint (0x00-0x7f)
        if (b <= 0x7f) return p + 1;
        // Negative fixint (0xe0-0xff)
        if (b >= 0xe0) return p + 1;
        // fixstr (0xa0-0xbf)
        if ((b & 0xe0) == 0xa0) { int len = b & 0x1f; return p + 1 + len; }
        // fixarray (0x90-0x9f)
        if ((b & 0xf0) == 0x90)
        {
            int count = b & 0x0f; p++;
            for (int i = 0; i < count; i++) { p = SkipValue(d, p); if (p < 0) return -1; }
            return p;
        }
        // fixmap (0x80-0x8f)
        if ((b & 0xf0) == 0x80)
        {
            int count = b & 0x0f; p++;
            for (int i = 0; i < count * 2; i++) { p = SkipValue(d, p); if (p < 0) return -1; }
            return p;
        }

        switch (b)
        {
            case 0xc0: return p + 1; // nil
            case 0xc2: case 0xc3: return p + 1; // false, true
            case 0xcc: return p + 2; // uint8
            case 0xcd: return p + 3; // uint16
            case 0xce: return p + 5; // uint32
            case 0xcf: return p + 9; // uint64
            case 0xd0: return p + 2; // int8
            case 0xd1: return p + 3; // int16
            case 0xd2: return p + 5; // int32
            case 0xd3: return p + 9; // int64
            case 0xca: return p + 5; // float32
            case 0xcb: return p + 9; // float64
            case 0xd9: return p + 2 + d[p + 1]; // str8
            case 0xda: return p + 3 + ((d[p + 1] << 8) | d[p + 2]); // str16
            case 0xdb: return p + 5 + ((d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]); // str32
            case 0xc4: return p + 2 + d[p + 1]; // bin8
            case 0xc5: return p + 3 + ((d[p + 1] << 8) | d[p + 2]); // bin16
            case 0xdc: // array16
            {
                int count = (d[p + 1] << 8) | d[p + 2]; p += 3;
                for (int i = 0; i < count; i++) { p = SkipValue(d, p); if (p < 0) return -1; }
                return p;
            }
            case 0xdd: // array32
            {
                int count = (d[p + 1] << 24) | (d[p + 2] << 16) | (d[p + 3] << 8) | d[p + 4]; p += 5;
                for (int i = 0; i < count; i++) { p = SkipValue(d, p); if (p < 0) return -1; }
                return p;
            }
            case 0xde: // map16
            {
                int count = (d[p + 1] << 8) | d[p + 2]; p += 3;
                for (int i = 0; i < count * 2; i++) { p = SkipValue(d, p); if (p < 0) return -1; }
                return p;
            }
        }
        return -1;
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
