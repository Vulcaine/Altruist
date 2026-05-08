using System.Buffers.Binary;
using Altruist;
using Altruist.Client;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Verifies <see cref="ClientPacketDispatcher"/> attribute discovery and
/// dispatch — single handler, multi-handler (multi-sub), unregistered MC,
/// MessageCode auto-detection from <see cref="IPacketBase.MessageCode"/>.
/// </summary>
public sealed class PacketDispatcherTests
{
    [MessagePackObject]
    public sealed class FooPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 4001;
        [Key(1)] public int X { get; set; }
    }

    [MessagePackObject]
    public sealed class BarPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 4002;
        [Key(1)] public string Text { get; set; } = "";
    }

    [PacketHandler]
    public sealed class TwoTypeHandler
    {
        public FooPacket? Foo { get; private set; }
        public BarPacket? Bar { get; private set; }

        [Packet(typeof(FooPacket))]
        public void OnFoo(FooPacket f) => Foo = f;

        [Packet(typeof(BarPacket))]
        public void OnBar(BarPacket b) => Bar = b;
    }

    [PacketHandler]
    public sealed class FooLogger
    {
        public int CallCount { get; private set; }

        [Packet(typeof(FooPacket))]
        public void Log(FooPacket _) => CallCount++;
    }

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    private static byte[] EncodeEnvelope<T>(T pkt) where T : IPacketBase =>
        MessagePackSerializer.Serialize(new MessageEnvelope(pkt, "rcv"), ServerOptions);

    [Fact]
    public void Dispatcher_RoutesByMessageCode_AcrossDistinctHandlers()
    {
        var dispatcher = new ClientPacketDispatcher();
        var handler = new TwoTypeHandler();
        dispatcher.Register(handler);
        var codec = new MessagePackClientCodec();

        dispatcher.Dispatch(EncodeEnvelope(new FooPacket { X = 9 }), codec);
        dispatcher.Dispatch(EncodeEnvelope(new BarPacket { Text = "hi" }), codec);

        Assert.NotNull(handler.Foo);
        Assert.Equal(9, handler.Foo!.X);
        Assert.NotNull(handler.Bar);
        Assert.Equal("hi", handler.Bar!.Text);
    }

    [Fact]
    public void MultipleHandlers_ForSameMessageCode_AllFire()
    {
        var dispatcher = new ClientPacketDispatcher();
        var primary = new TwoTypeHandler();
        var logger = new FooLogger();
        dispatcher.Register(primary);
        dispatcher.Register(logger);
        var codec = new MessagePackClientCodec();

        dispatcher.Dispatch(EncodeEnvelope(new FooPacket { X = 7 }), codec);

        Assert.Equal(7, primary.Foo!.X);
        Assert.Equal(1, logger.CallCount);
    }

    [Fact]
    public void Dispatch_OfUnregisteredMessageCode_LogsAndSkips()
    {
        var dispatcher = new ClientPacketDispatcher();
        var logs = new List<string>();
        dispatcher.Logger = msg => logs.Add(msg);
        var codec = new MessagePackClientCodec();

        // No handlers registered for FooPacket — dispatch should warn but not throw.
        dispatcher.Dispatch(EncodeEnvelope(new FooPacket { X = 0 }), codec);

        Assert.Contains(logs, m => m.Contains("Unhandled MC=4001", StringComparison.Ordinal));
    }

    [Fact]
    public void Register_OnHandler_ProducesRegistrationKeyedByPacketDefaultMC()
    {
        var dispatcher = new ClientPacketDispatcher();
        dispatcher.Register(new TwoTypeHandler());

        Assert.Equal(1, dispatcher.HandlerCountForMC(4001));
        Assert.Equal(1, dispatcher.HandlerCountForMC(4002));
        Assert.Equal(0, dispatcher.HandlerCountForMC(9999));
    }

    [Fact]
    public void Dispatch_OnGarbageBytes_LogsAndDoesNotThrow()
    {
        var dispatcher = new ClientPacketDispatcher();
        var logs = new List<string>();
        dispatcher.Logger = msg => logs.Add(msg);
        var codec = new MessagePackClientCodec();

        dispatcher.Dispatch(new byte[] { 0xff, 0xee }, codec);

        Assert.Contains(logs, m => m.Contains("Unrecognised payload", StringComparison.Ordinal));
    }
}
