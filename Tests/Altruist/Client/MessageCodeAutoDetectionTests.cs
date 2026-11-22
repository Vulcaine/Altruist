using Altruist;
using Altruist.Client;
using MessagePack;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Verifies the dispatcher's MessageCode auto-detection contract: the framework
/// reads <see cref="IPacketBase.MessageCode"/> from a default-constructed
/// instance of the packet type. If the contract is violated (no parameterless
/// ctor, or default MessageCode == 0), registration MUST fail fast at boot
/// rather than silently miss frames at runtime.
/// </summary>
public sealed class MessageCodeAutoDetectionTests
{
    [MessagePackObject]
    public sealed class WellFormedPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 7777;
        [Key(1)] public int Value { get; set; }
    }

    [MessagePackObject]
    public sealed class ZeroDefaultMcPacket : IPacketBase
    {
        // No initializer → MessageCode defaults to 0 → registration must fail
        [Key(0)] public uint MessageCode { get; set; }
        [Key(1)] public int Value { get; set; }
    }

    // Intentionally NOT [MessagePackObject] — we never serialize this type;
    // we only confirm the dispatcher rejects it at Register time.
    public sealed class NoCtorPacket : IPacketBase
    {
        public uint MessageCode { get; set; } = 8888;
        public int Value { get; set; }

        // No public parameterless ctor — registration must fail
        public NoCtorPacket(int value) { Value = value; }
    }

    [PacketHandler]
    public sealed class WellFormedHandler
    {
        [Packet(typeof(WellFormedPacket))]
        public void Handle(WellFormedPacket _) { }
    }

    [PacketHandler]
    public sealed class ZeroMcHandler
    {
        [Packet(typeof(ZeroDefaultMcPacket))]
        public void Handle(ZeroDefaultMcPacket _) { }
    }

    [PacketHandler]
    public sealed class NoCtorHandler
    {
        [Packet(typeof(NoCtorPacket))]
        public void Handle(NoCtorPacket _) { }
    }

    [Fact]
    public void Register_AutoDetectsMessageCode_FromDefaultInstance()
    {
        var dispatcher = new ClientPacketDispatcher();
        dispatcher.Register(new WellFormedHandler());

        Assert.Equal(1, dispatcher.HandlerCountForMC(7777));
    }

    [Fact]
    public void Register_FailsFast_WhenPacketDefaultMessageCodeIsZero()
    {
        var dispatcher = new ClientPacketDispatcher();
        var ex = Assert.Throws<InvalidOperationException>(
            () => dispatcher.Register(new ZeroMcHandler()));

        Assert.Contains("MessageCode is 0", ex.Message);
    }

    [Fact]
    public void Register_FailsFast_WhenPacketHasNoParameterlessCtor()
    {
        var dispatcher = new ClientPacketDispatcher();
        var ex = Assert.Throws<InvalidOperationException>(
            () => dispatcher.Register(new NoCtorHandler()));

        Assert.Contains("parameterless constructor", ex.Message);
    }
}
