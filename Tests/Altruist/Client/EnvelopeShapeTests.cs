using Altruist;
using Altruist.Client;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Asserts <see cref="MessageEnvelopeShape"/> peeks the right MessageCode and
/// slices the inner message bytes correctly out of a real server-encoded envelope.
/// </summary>
public sealed class EnvelopeShapeTests
{
    [MessagePackObject]
    public sealed class FooPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 4242;
        [Key(1)] public string Name { get; set; } = "";
        [Key(2)] public int Score { get; set; }
    }

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    [Fact]
    public void PeekMessageCode_ReturnsExpectedMC_ForFixarray3Envelope()
    {
        var pkt = new FooPacket { Name = "hello", Score = 9 };
        var bytes = MessagePackSerializer.Serialize(new MessageEnvelope(pkt, "rcv"), ServerOptions);

        Assert.Equal((uint)4242, MessageEnvelopeShape.PeekMessageCode(bytes));
    }

    [Fact]
    public void PeekMessageCode_ReturnsZero_OnGarbageInput()
    {
        Assert.Equal((uint)0, MessageEnvelopeShape.PeekMessageCode(new byte[] { 0xff, 0xee }));
        Assert.Equal((uint)0, MessageEnvelopeShape.PeekMessageCode(Array.Empty<byte>()));
    }

    [Fact]
    public void ExtractMessage_RoundTripsToOriginalPayload()
    {
        var pkt = new FooPacket { Name = "world", Score = 42 };
        var envelope = MessagePackSerializer.Serialize(new MessageEnvelope(pkt, "rcv"), ServerOptions);

        var inner = MessageEnvelopeShape.ExtractMessage(envelope);
        Assert.NotEmpty(inner);

        var decoded = new MessagePackClientCodec().Deserialize<FooPacket>(inner);
        Assert.NotNull(decoded);
        Assert.Equal((uint)4242, decoded!.MessageCode);
        Assert.Equal("world", decoded.Name);
        Assert.Equal(42, decoded.Score);
    }

    [Fact]
    public void ExtractMessage_ReturnsEmpty_OnNonFixArray3Header()
    {
        // Valid fixarray header would be 0x93. 0x91 is fixarray(1).
        Assert.Empty(MessageEnvelopeShape.ExtractMessage(new byte[] { 0x91, 0x00 }));
    }
}
