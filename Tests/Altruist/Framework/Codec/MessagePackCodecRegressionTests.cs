/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Buffers;

using Altruist;
using Altruist.Codec.MessagePack;

using MessagePack;

namespace Tests.Altruist.Framework.Codec;

/// <summary>
/// Regressions for the MessagePack codec: hostile inbound payloads (deep nesting overflowed the
/// stack inside MessagePack's skip path and killed the process; oversized declared lengths), and
/// the buffer-writer encode path (IBufferEncoder).
/// </summary>
public sealed class MessagePackCodecRegressionTests
{
    [MessagePackObject]
    public sealed class SamplePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 7;
        [Key(1)] public int Seq { get; set; }
        [Key(2)] public string Text { get; set; } = "";
        [Key(3)] public float X { get; set; }
    }

    private static readonly MessagePackMessageDecoder Decoder = new();
    private static readonly MessagePackMessageEncoder Encoder = new();

    private static byte[] Sample(int seq) => MessagePackSerializer.Serialize(new SamplePacket { Seq = seq, Text = "hi", X = 1.5f });

    /// <summary>A sample packet array with one extra trailing element nested <paramref name="depth"/> arrays deep.</summary>
    private static byte[] SampleWithNestedExtra(int depth)
    {
        var head = Sample(1);
        Assert.Equal(0x94, head[0]); // fixarray(4)
        var bytes = new byte[head.Length + depth + 1];
        head.CopyTo(bytes, 0);
        bytes[0] = 0x95; // fixarray(5): the unknown 5th element is skipped by the formatter
        bytes.AsSpan(head.Length, depth).Fill(0x91); // fixarray(1) ...
        bytes[^1] = 0xc0; // nil
        return bytes;
    }

    private static byte[] NestedArrays(int levels)
    {
        var b = new byte[levels + 1];
        b.AsSpan(0, levels).Fill(0x91);
        b[^1] = 0x01;
        return b;
    }

    [Fact]
    public void Deeply_nested_payload_is_rejected_instead_of_overflowing_the_stack()
    {
        // ~200 KB of nested arrays. Without the structure guard this crashes the test host with
        // an uncatchable StackOverflowException (MessagePackReader skip recursion).
        var bomb = SampleWithNestedExtra(200_000);
        Assert.Throws<MessagePackSerializationException>(() => Decoder.Decode<IPacket>(bomb, typeof(SamplePacket)));
        Assert.Throws<MessagePackSerializationException>(() => Decoder.Decode(bomb, typeof(SamplePacket)));
        Assert.Throws<MessagePackSerializationException>(() => Decoder.Decode<SamplePacket>(bomb));
    }

    [Fact]
    public void Ordinary_and_moderately_nested_packets_still_decode()
    {
        var p = Decoder.Decode<SamplePacket>(Sample(42), typeof(SamplePacket));
        Assert.Equal(42, p.Seq);
        Assert.Equal("hi", p.Text);
        // An unknown extra field nested 20 levels deep is skipped as before.
        Assert.Equal(1, Decoder.Decode<SamplePacket>(SampleWithNestedExtra(20), typeof(SamplePacket)).Seq);
    }

    [Fact]
    public void Guard_accepts_exactly_MaxDepth_levels_and_rejects_one_more()
    {
        MessagePackStructureGuard.Validate(NestedArrays(MessagePackStructureGuard.MaxDepth));
        Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackStructureGuard.Validate(NestedArrays(MessagePackStructureGuard.MaxDepth + 1)));
    }

    [Fact]
    public void Guard_counts_maps_as_nesting()
    {
        // {1: {1: {1: ... nil}}} 40 levels deep.
        var maps = Enumerable.Repeat(new byte[] { 0x81, 0x01 }, 40).SelectMany(x => x).Append((byte)0xc0).ToArray();
        Assert.Throws<MessagePackSerializationException>(() => MessagePackStructureGuard.Validate(maps));
    }

    [Fact]
    public void Guard_rejects_declared_lengths_larger_than_the_payload()
    {
        // array32 declaring 2^31-1 elements in an 8-byte payload.
        Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackStructureGuard.Validate(new byte[] { 0xdd, 0x7f, 0xff, 0xff, 0xff, 0x01, 0x02, 0x03 }));
        // map32 declaring 2^31-1 pairs.
        Assert.Throws<MessagePackSerializationException>(() =>
            MessagePackStructureGuard.Validate(new byte[] { 0xdf, 0x7f, 0xff, 0xff, 0xff, 0x01, 0x02 }));
        // The decoder applies the guard before deserializing.
        Assert.Throws<MessagePackSerializationException>(() =>
            Decoder.Decode<IPacket>(new byte[] { 0xdd, 0x7f, 0xff, 0xff, 0xff, 0x01 }, typeof(SamplePacket)));
    }

    [Fact]
    public void Guard_reports_truncated_input_as_a_serialization_error()
    {
        // str8 declaring 32 bytes, 1 present.
        Assert.Throws<MessagePackSerializationException>(() => MessagePackStructureGuard.Validate(new byte[] { 0xd9, 0x20, 0x41 }));
        // fixarray(3) with only one element.
        Assert.Throws<MessagePackSerializationException>(() => MessagePackStructureGuard.Validate(new byte[] { 0x93, 0x01 }));
    }

    [Fact]
    public void Guard_accepts_wide_flat_input_and_empty_input()
    {
        MessagePackStructureGuard.Validate(MessagePackSerializer.Serialize(Enumerable.Range(0, 10_000).ToArray()));
        MessagePackStructureGuard.Validate(ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void Encoder_writes_into_a_caller_owned_buffer_with_the_same_bytes()
    {
        Assert.IsAssignableFrom<IBufferEncoder>(Encoder);

        var packet = new SamplePacket { Seq = 9, Text = "buffer", X = -2.25f };
        var expected = Encoder.Encode(packet);

        var writer = new ArrayBufferWriter<byte>();
        ((IBufferEncoder)Encoder).Encode(writer, packet);
        Assert.Equal(expected, writer.WrittenSpan.ToArray());

        // Reusing the same writer after Clear produces the next message only.
        writer.Clear();
        ((IBufferEncoder)Encoder).Encode(writer, new SamplePacket { Seq = 10 });
        Assert.Equal(10, Decoder.Decode<SamplePacket>(writer.WrittenSpan.ToArray()).Seq);
    }

    [Fact]
    public void Codec_registered_for_messagepack_exposes_the_guarded_decoder_and_buffer_encoder()
    {
        var codec = new MessagePackCodec();
        Assert.IsType<MessagePackMessageDecoder>(codec.Decoder);
        Assert.IsAssignableFrom<IBufferEncoder>(codec.Encoder);
        Assert.Throws<MessagePackSerializationException>(() =>
            codec.Decoder.Decode<IPacket>(NestedArrays(MessagePackStructureGuard.MaxDepth + 5), typeof(SamplePacket)));
    }
}
