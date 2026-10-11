/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text;
using System.Threading.Channels;

using Altruist;
using Altruist.Codec;
using Altruist.Codec.MessagePack;

using MessagePack;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// ConnectionManager lifecycle and codec selection: interceptors for every gate shape, one disconnect path per
/// connection, the waiting room before the first connection, per-transport codecs, and the global codec.
/// </summary>
public sealed class ConnectionLifecycleRegressionTests
{
    [MessagePackObject]
    public sealed class PingPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 78;
        [Key(1)] public int N { get; set; }
    }

    /// <summary>A connection fed from a channel; closing it ends the read loop like a peer close.</summary>
    private sealed class ChannelConnection : AltruistConnection
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly string? _mode;
        public int Closes;

        public ChannelConnection(string route, string? mode = null)
        {
            Route = route;
            _mode = mode;
            SetId("c-" + Guid.NewGuid().ToString("N"));
        }

        public override string? TransportMode => _mode;
        public override bool IsConnected => true;
        public void Push(byte[] frame) => _inbound.Writer.TryWrite(frame);

        public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            try { return await _inbound.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return Array.Empty<byte>(); }
        }

        public override Task CloseOutputAsync() => Task.CompletedTask;

        public override Task CloseAsync()
        {
            Interlocked.Increment(ref Closes);
            _inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }

    private sealed class CountingPortal : IPortal, OnDisconnectedAsync
    {
        public CountingPortal(string route) => Route = route;
        public string Route { get; set; }
        public int Disconnects;
        public Task OnDisconnectedAsync(string clientId, Exception? exception)
        {
            Interlocked.Increment(ref Disconnects);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectAll : IInterceptor
    {
        public int Calls;
        public Task Intercept(InterceptContext context, IPacket eventData)
        {
            Interlocked.Increment(ref Calls);
            context.Reject();
            return Task.CompletedTask;
        }
    }

    private static Mock<ISocketManager> Sockets(ChannelConnection? connection = null, Task<RoomPacket>? waitingRoom = null)
    {
        var sockets = new Mock<ISocketManager>();
        sockets.Setup(s => s.CreateRoomAsync(It.IsAny<string?>())).Returns(waitingRoom ?? Task.FromResult(new RoomPacket()));
        sockets.Setup(s => s.AddConnectionAsync(It.IsAny<string>(), It.IsAny<AltruistConnection>(), It.IsAny<string?>())).ReturnsAsync(true);
        sockets.Setup(s => s.GetConnectionAsync(It.IsAny<string>())).ReturnsAsync(connection);
        return sockets;
    }

    private static Mock<ICodecResolver> Codecs(ICodec global, ICodec? forConnections = null)
    {
        var codecs = new Mock<ICodecResolver>();
        codecs.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(global);
        codecs.Setup(c => c.ResolveForConnection(It.IsAny<AltruistConnection>())).Returns(forConnections ?? global);
        return codecs;
    }

    private static ConnectionManager Manager(Mock<ISocketManager> sockets, Mock<ICodecResolver>? codecs = null, params IInterceptor[] interceptors) =>
        new(sockets.Object, (codecs ?? Codecs(new MessagePackCodec())).Object, NullLoggerFactory.Instance, timeout: 30,
            interceptors: interceptors);

    private static AltruistPacket Packet(string evt) => new() { Event = evt, MessageCode = PacketCodes.Altruist };

    private static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Interceptors_can_reject_gates_without_a_packet_parameter()
    {
        var calls = 0;
        var noArgs = UniqueName("no-args-");
        var clientOnly = UniqueName("client-only-");
        PortalGateRegistry<IPortal>.Register(noArgs, new Func<Task>(() => { calls++; return Task.CompletedTask; }));
        PortalGateRegistry<IPortal>.Register(clientOnly, new Func<string, Task>(_ => { calls++; return Task.CompletedTask; }));
        var reject = new RejectAll();
        var manager = Manager(Sockets(), interceptors: reject);

        await manager.ProcessPacket(Packet(noArgs), Array.Empty<byte>(), "/game", "c1");
        await manager.ProcessPacket(Packet(clientOnly), Array.Empty<byte>(), "/game", "c1");

        Assert.Equal(0, calls);
        Assert.Equal(2, reject.Calls);
    }

    [Fact]
    public async Task Disconnecting_a_client_runs_the_disconnect_hooks_once()
    {
        var route = "/" + UniqueName("route-");
        var portal = new CountingPortal(route);
        PortalGateRegistry<IPortal>.RegisterInstance(portal);
        var connection = new ChannelConnection(route);
        var sockets = Sockets(connection);
        var manager = Manager(sockets);

        var lifetime = manager.HandleConnection(connection, route, connection.ConnectionId);
        await WaitUntil(() => sockets.Invocations.Any(i => i.Method.Name == nameof(ISocketManager.AddConnectionAsync)));
        await Task.Delay(50);
        await manager.DisconnectAsync(connection.ConnectionId);
        await lifetime.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, portal.Disconnects);
    }

    [Fact]
    public async Task Connections_wait_for_the_waiting_room()
    {
        var room = new TaskCompletionSource<RoomPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new ChannelConnection("/" + UniqueName("route-"));
        var sockets = Sockets(connection, room.Task);
        var manager = Manager(sockets);

        var lifetime = manager.HandleConnection(connection, connection.Route, connection.ConnectionId);
        await Task.Delay(100);
        sockets.Verify(s => s.AddConnectionAsync(It.IsAny<string>(), It.IsAny<AltruistConnection>(), It.IsAny<string?>()), Times.Never);

        room.SetResult(new RoomPacket());
        await WaitUntil(() => sockets.Invocations.Any(i => i.Method.Name == nameof(ISocketManager.AddConnectionAsync)));
        await manager.DisconnectAsync(connection.ConnectionId);
        await lifetime.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_connection_is_read_with_its_transport_codec()
    {
        PingPacket? received = null;
        var evt = UniqueName("ping-");
        PortalGateRegistry<IPortal>.Register(evt, new Func<PingPacket, string, Task>((p, _) => { received = p; return Task.CompletedTask; }));
        var json = new JsonCodec(new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var connection = new ChannelConnection("/" + UniqueName("route-"), "websocket");
        var manager = Manager(Sockets(connection), Codecs(new MessagePackCodec(), json));

        var lifetime = manager.HandleConnection(connection, connection.Route, connection.ConnectionId);
        connection.Push(Encoding.UTF8.GetBytes("{\"event\":\"" + evt + "\",\"data\":{\"messageCode\":78,\"n\":5}}"));
        await WaitUntil(() => received is not null);
        await manager.DisconnectAsync(connection.ConnectionId);
        await lifetime.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(5, received!.N);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }
}

public sealed class CodecResolutionRegressionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static IServiceProvider Services() =>
        new ServiceCollection().AddSingleton(new System.Text.Json.JsonSerializerOptions()).BuildServiceProvider();

    private static CodecResolver Resolver(IConfiguration config) => new(Services(), config, NullLoggerFactory.Instance);

    private sealed class TcpConnection : AltruistConnection
    {
        public override string? TransportMode => "tcp";
    }

    [Fact]
    public void The_global_codec_is_the_configured_provider()
    {
        Assert.IsType<JsonCodec>(CodecProviders.GlobalCodec(Services(), "json"));
        Assert.IsType<MessagePackCodec>(CodecProviders.GlobalCodec(Services(), "MessagePack"));
        Assert.Throws<InvalidOperationException>(() => CodecProviders.GlobalCodec(Services(), "nope"));
    }

    [Fact]
    public void Connections_resolve_their_transport_codec_by_mode_not_by_class_name()
    {
        var resolver = Resolver(Config(("altruist:server:transport:tcp:codec:provider", "json")));
        Assert.IsType<JsonCodec>(resolver.ResolveForConnection(new TcpConnection()));
        Assert.IsType<MessagePackCodec>(resolver.ResolveForConnection(new AltruistConnection()));
    }

    [Fact]
    public void Unconfigured_resolves_messagepack_and_unknown_names_fail()
    {
        Assert.IsType<MessagePackCodec>(Resolver(Config()).Resolve());
        Assert.Throws<InvalidOperationException>(() => Resolver(Config(("altruist:server:transport:codec:provider", "nope"))).Resolve());
    }

    [Fact]
    public void The_json_encoder_uses_the_decoder_options()
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var codec = new JsonCodec(options);
        var json = Encoding.UTF8.GetString(codec.Encoder.Encode(new ConnectionLifecycleRegressionTests.PingPacket { N = 3 }));
        Assert.Contains("\"n\":3", json);
    }
}
