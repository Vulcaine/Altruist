using Altruist;
using Altruist.Client;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Demonstrates the AOT-safe codec wiring: build a <see cref="MessagePackClientCodec"/>
/// from a constrained options bag (StandardResolver only — no typeless / no
/// reflection-emit fallback) and round-trip a known concrete packet type T.
///
/// <para>This is the pattern Unity / IL2CPP / NativeAOT consumers should follow:
/// pre-register their packet formatters in a composite resolver and pass it to
/// the codec ctor. Game-specific packets without registered formatters would
/// throw on serialize / deserialize — exactly the error a stripped-runtime build
/// would surface.</para>
/// </summary>
public sealed class AotCodecTests
{
    [MessagePackObject]
    public sealed class AotPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 999;
        [Key(1)] public int X { get; set; }
        [Key(2)] public int Y { get; set; }
    }

    [Fact]
    public void Codec_WithStandardResolverOnly_RoundTripsKnownPacketType()
    {
        // No typeless / no AllowPrivate fallback — closest to what an AOT-built
        // composite resolver would have. AotPacket has [Key]'d fields so the
        // standard resolver can build a formatter for it via the source generator
        // path on AOT (or via reflection at JIT time, which still works on .NET test host).
        var options = MessagePackSerializerOptions.Standard.WithResolver(StandardResolver.Instance);
        var codec = new MessagePackClientCodec(options);

        var pkt = new AotPacket { X = 10, Y = -42 };
        var bytes = codec.Serialize(pkt);
        var roundTripped = codec.Deserialize<AotPacket>(bytes);

        Assert.NotNull(roundTripped);
        Assert.Equal(10, roundTripped!.X);
        Assert.Equal(-42, roundTripped.Y);
    }

    [Fact]
    public void Default_Codec_Singleton_IsUsable_OnHotPath()
    {
        // Default singleton should work without ctor wiring for the simple case.
        var codec = new MessagePackClientCodec();
        var pkt = new AotPacket { X = 1, Y = 2 };
        var bytes = codec.Serialize(pkt);
        var back = codec.Deserialize<AotPacket>(bytes);
        Assert.Equal(1, back!.X);
        Assert.Equal(2, back.Y);
    }
}
