/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Codec.MessagePack;

using MessagePack;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// ConnectionManager.ProcessPacket regressions: interceptors finish before the gate handler and
/// can veto the packet; undecodable payloads are dropped instead of handing the handler a
/// default-constructed packet; packets for unknown events still pass the interceptors.
/// </summary>
public sealed class ConnectionManagerInterceptorTests
{
    [MessagePackObject]
    public sealed class MovePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 77;
        [Key(1)] public int X { get; set; }
        [Key(2)] public int Y { get; set; }
    }

    private sealed class RecordingInterceptor : IInterceptor
    {
        private readonly Func<InterceptContext, IPacket?, Task> _body;
        public RecordingInterceptor(Func<InterceptContext, IPacket?, Task> body) => _body = body;
        public List<(InterceptContext Context, IPacket? Packet)> Calls { get; } = new();

        public async Task Intercept(InterceptContext context, IPacket eventData)
        {
            lock (Calls)
                Calls.Add((context, eventData));
            await _body(context, eventData);
        }
    }

    private static ConnectionManager NewManager(params IInterceptor[] interceptors)
    {
        var codecs = new Mock<ICodecResolver>();
        codecs.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(new MessagePackCodec());
        var manager = new ConnectionManager(new Mock<ISocketManager>().Object, codecs.Object, NullLoggerFactory.Instance);
        foreach (var i in interceptors)
            manager.AddInterceptor(i);
        return manager;
    }

    /// <summary>Registers a gate under a unique event name (the registry is process-wide).</summary>
    private static string RegisterGate(Func<MovePacket, string, Task> handler)
    {
        var name = "test-move-" + Guid.NewGuid().ToString("N");
        PortalGateRegistry<IPortal>.Register(name, handler);
        return name;
    }

    private static byte[] Payload(int x = 1, int y = 2) => MessagePackSerializer.Serialize(new MovePacket { X = x, Y = y });

    private static AltruistPacket Packet(string evt) => new() { Event = evt, MessageCode = PacketCodes.Altruist };

    [Fact]
    public async Task A_rejecting_interceptor_vetoes_the_handler()
    {
        var handled = 0;
        var evt = RegisterGate((_, _) => { Interlocked.Increment(ref handled); return Task.CompletedTask; });
        var manager = NewManager(new RecordingInterceptor((ctx, _) => { ctx.Reject(); return Task.CompletedTask; }));

        Assert.True(await manager.ProcessPacket(Packet(evt), Payload(), "/game", "client-1"));
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task One_rejection_among_several_interceptors_vetoes_the_handler()
    {
        var handled = 0;
        var evt = RegisterGate((_, _) => { handled++; return Task.CompletedTask; });
        var allow = new RecordingInterceptor((_, _) => Task.CompletedTask);
        var reject = new RecordingInterceptor(async (ctx, _) => { await Task.Delay(10); ctx.Reject(); });
        var manager = NewManager(allow, reject);

        await manager.ProcessPacket(Packet(evt), Payload(), "/game", "client-1");

        Assert.Equal(0, handled);
        Assert.Single(allow.Calls);
        Assert.Single(reject.Calls);
    }

    [Fact]
    public async Task Interceptors_complete_before_the_handler_starts()
    {
        var interceptorDone = false;
        bool? seenByHandler = null;
        var evt = RegisterGate((_, _) => { seenByHandler = interceptorDone; return Task.CompletedTask; });
        var manager = NewManager(new RecordingInterceptor(async (_, _) =>
        {
            await Task.Delay(50);
            interceptorDone = true;
        }));

        await manager.ProcessPacket(Packet(evt), Payload(), "/game", "client-1");

        // Before the fix the interceptor task was awaited only after the handler had run.
        Assert.True(seenByHandler);
    }

    [Fact]
    public async Task Allowed_packets_reach_the_handler_and_the_context_describes_them()
    {
        MovePacket? received = null;
        string? receivedClient = null;
        var evt = RegisterGate((p, c) => { received = p; receivedClient = c; return Task.CompletedTask; });
        var interceptor = new RecordingInterceptor((_, _) => Task.CompletedTask);
        var manager = NewManager(interceptor);
        var payload = Payload(5, -6);

        Assert.True(await manager.ProcessPacket(Packet(evt), payload, "/game", "client-7"));

        Assert.Equal(5, received!.X);
        Assert.Equal(-6, received.Y);
        Assert.Equal("client-7", receivedClient);
        var (ctx, packet) = Assert.Single(interceptor.Calls);
        Assert.Equal(evt, ctx.EventName);
        Assert.Equal("client-7", ctx.ClientId);
        Assert.Equal(payload.Length, ctx.PayloadLength);
        Assert.False(ctx.Rejected);
        Assert.IsType<MovePacket>(packet);
    }

    [Fact]
    public async Task Undecodable_payload_is_dropped_keeps_the_connection_and_still_passes_the_interceptors()
    {
        var handled = 0;
        var evt = RegisterGate((_, _) => { handled++; return Task.CompletedTask; });
        var interceptor = new RecordingInterceptor((_, _) => Task.CompletedTask);
        var manager = NewManager(interceptor);

        // A string where an int array is expected, and a nesting bomb.
        var garbage = MessagePackSerializer.Serialize("not a move packet");
        Assert.True(await manager.ProcessPacket(Packet(evt), garbage, "/game", "client-1"));
        var bomb = Enumerable.Repeat((byte)0x91, 10_000).Append((byte)0xc0).ToArray();
        Assert.True(await manager.ProcessPacket(Packet(evt), bomb, "/game", "client-1"));

        Assert.Equal(0, handled);
        Assert.Equal(2, interceptor.Calls.Count);
        Assert.All(interceptor.Calls, c => Assert.Null(c.Packet));
    }

    [Fact]
    public async Task Unknown_events_still_pass_the_interceptors_with_a_null_payload()
    {
        var interceptor = new RecordingInterceptor((_, _) => Task.CompletedTask);
        var manager = NewManager(interceptor);
        var evt = "no-such-gate-" + Guid.NewGuid().ToString("N");

        Assert.True(await manager.ProcessPacket(Packet(evt), new byte[] { 1, 2, 3 }, "/game", "client-3"));

        var (ctx, packet) = Assert.Single(interceptor.Calls);
        Assert.Equal(evt, ctx.EventName);
        Assert.Equal("client-3", ctx.ClientId);
        Assert.Equal(3, ctx.PayloadLength);
        Assert.Null(packet);
    }

    [Fact]
    public async Task Relay_interceptor_ignores_packets_without_payload()
    {
        var relay = new Mock<IRelayService>();
        var interceptor = new RelayInterceptor(relay.Object);

        await interceptor.Intercept(new InterceptContext("unknown"), null!);
        relay.Verify(r => r.Relay(It.IsAny<IPacket>()), Times.Never);

        await interceptor.Intercept(new InterceptContext("known"), new MovePacket());
        relay.Verify(r => r.Relay(It.IsAny<IPacket>()), Times.Once);
    }

    [Fact]
    public void InterceptContext_defaults_and_reject()
    {
        var ctx = new InterceptContext("evt");
        Assert.Equal("", ctx.ClientId);
        Assert.Equal(0, ctx.PayloadLength);
        Assert.False(ctx.Rejected);
        ctx.Reject();
        Assert.True(ctx.Rejected);
    }
}
