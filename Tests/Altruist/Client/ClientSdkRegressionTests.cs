using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Altruist;
using Altruist.Client;
using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;

using MessagePack;
using MessagePack.Resolvers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Client;

/// <summary>Regressions of the C# client SDK: wire shapes, handler discovery, codecs and transports.</summary>
public sealed class ClientSdkRegressionTests
{
    [MessagePackObject]
    public sealed class ScorePacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 4101;
        [Key(1)] public int Score { get; set; }
    }

    [MessagePackObject]
    public sealed class Holder
    {
        [Key(0)] public IPacketBase? Inner { get; set; }
    }

    public sealed class ScoreBoard
    {
        public readonly List<int> Scores = new();
    }

    /// <summary>A handler with a DI dependency: only the application's provider can build it.</summary>
    public sealed class ScoreHandler
    {
        private readonly ScoreBoard _board;
        public ScoreHandler(ScoreBoard board) => _board = board;

        [Packet(typeof(ScorePacket))]
        public void On(ScorePacket p) => _board.Scores.Add(p.Score);
    }

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(StandardResolverAllowPrivate.Instance, TypelessContractlessStandardResolver.Instance));

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Interface_typed_packet_members_round_trip()
    {
        var bytes = MessagePackSerializer.Serialize(new Holder { Inner = new ScorePacket { Score = 9 } });
        var back = MessagePackSerializer.Deserialize<Holder>(bytes);
        Assert.Equal(9, Assert.IsType<ScorePacket>(back.Inner).Score);
    }

    [Fact]
    public void Interface_typed_members_refuse_non_packet_type_names()
    {
        var writer = new ArrayBufferWriterBytes();
        var w = new MessagePackWriter(writer);
        w.WriteArrayHeader(1);
        w.WriteArrayHeader(2);
        w.Write(typeof(Uri).AssemblyQualifiedName);
        w.Write("http://example.com");
        w.Flush();
        Assert.ThrowsAny<Exception>(() => MessagePackSerializer.Deserialize<Holder>(writer.ToArray()));
    }

    private sealed class ArrayBufferWriterBytes : System.Buffers.IBufferWriter<byte>
    {
        private readonly System.Buffers.ArrayBufferWriter<byte> _inner = new();
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public byte[] ToArray() => _inner.WrittenSpan.ToArray();
    }

    [Fact]
    public void Discovered_handlers_are_the_application_singletons()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ScoreBoard>();
        ClientPacketHandlerConfig.Register(services, new[] { typeof(ScoreHandler) });
        services.AddSingleton(sp => new ClientPacketDispatcher(sp));
        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<ClientPacketDispatcher>();
        dispatcher.Dispatch(MessagePackSerializer.Serialize(new MessageEnvelope(new ScorePacket { Score = 42 }, "me"), ServerOptions),
            new MessagePackClientCodec());

        Assert.Equal(new[] { 42 }, provider.GetRequiredService<ScoreBoard>().Scores);
    }

    [Fact]
    public void Json_server_envelopes_are_dispatched_with_the_json_codec()
    {
        var board = new ScoreBoard();
        var dispatcher = new ClientPacketDispatcher();
        dispatcher.Register(new ScoreHandler(board));
        var frame = Encoding.UTF8.GetBytes("{\"messageCode\":4101,\"header\":{\"sender\":\"server\"},\"message\":{\"messageCode\":4101,\"score\":7}}");

        dispatcher.Dispatch(frame, new JsonClientCodec());

        Assert.Equal(new[] { 7 }, board.Scores);
    }

    [Fact]
    public void Inventory_forwarding_is_only_configured_with_a_client_transport()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.False(DependencyResolver.ShouldRegister(typeof(ClientInventoryServiceConfig), empty, NullLogger.Instance));
    }

    [Fact]
    public void Envelope_parser_reads_array16_and_array32_envelopes()
    {
        Assert.Equal(new byte[] { 0xcd, 0x10, 0x92 }, MessageEnvelopeShape.ExtractMessage(Hex("dc000301c0cd1092")));
        Assert.Equal(new byte[] { 0xc3 }, MessageEnvelopeShape.ExtractMessage(Hex("dd0000000301c0c3")));
        Assert.Empty(MessageEnvelopeShape.ExtractMessage(Hex("dc000201c0")));
    }

    [Theory]
    [InlineData("c600000002aabb", 7)]        // bin32
    [InlineData("df000000010101", 7)]        // map32 {1: 1}
    [InlineData("d401aa", 3)]                // fixext1
    [InlineData("c70201aabb", 5)]            // ext8
    [InlineData("c90000000101aa", 7)]        // ext32
    public void SkipValue_covers_every_messagepack_type(string hex, int end)
    {
        Assert.Equal(end, MessageEnvelopeShape.SkipValue(Hex(hex), 0));
    }

    [Theory]
    [InlineData("d9ff")]                     // str8 longer than the buffer
    [InlineData("db")]                       // str32 without its length
    [InlineData("dcffff")]                   // array16 with missing items
    [InlineData("dd7fffffff")]               // huge array32 count
    [InlineData("c1")]                       // never used
    public void SkipValue_rejects_truncated_or_invalid_input_without_throwing(string hex)
    {
        Assert.Equal(-1, MessageEnvelopeShape.SkipValue(Hex(hex), 0));
    }

    [Fact]
    public void SkipValue_rejects_nesting_bombs()
    {
        var bomb = Enumerable.Repeat((byte)0x91, 100_000).Append((byte)0xc0).ToArray();
        Assert.Equal(-1, MessageEnvelopeShape.SkipValue(bomb, 0));
    }

    [Fact]
    public void Items_wider_than_255_cells_do_not_fit_a_small_grid()
    {
        var svc = new ClientInventoryService();
        svc.RegisterContainer(new GridLayout(1, 5, 5));
        Assert.False(svc.CanAnchorItemAt(1, 0, 256, 1));
        Assert.False(svc.CanAnchorItemAt(1, 0, 1, 257));
    }

    [Fact]
    public void Gate_names_must_fit_the_servers_127_byte_limit()
    {
        Assert.Throws<ArgumentException>(() => ClientFrame.GateBytes(new string('g', 128)));
        Assert.Throws<ArgumentException>(() => ClientFrame.GateBytes(""));
        Assert.Equal(127, ClientFrame.GateBytes(new string('g', 127)).Length);
    }

    [Fact]
    public async Task Udp_client_survives_a_receive_timeout()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var client = new AltruistUdpClient(new EndpointConfig("127.0.0.1", port, "messagepack"), new MessagePackClientCodec());

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(new string('g', 200), new ScorePacket()));
        await client.SendAsync("hello", new ScorePacket { Score = 1 });
        var first = await server.ReceiveAsync();
        Assert.Equal(5, first.Buffer[0]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReceiveAsync(TimeSpan.FromMilliseconds(50)));
        await server.SendAsync(new byte[] { 1, 2, 3 }, 3, first.RemoteEndPoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, await client.ReceiveAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Tcp_connect_honours_cancellation()
    {
        // A non-routable address: the connect hangs until cancelled (or until the OS gives up much later).
        using var client = new AltruistTcpClient(new EndpointConfig("10.255.255.1", 9, "messagepack"), new MessagePackClientCodec());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;

        var ex = await Record.ExceptionAsync(() => client.ConnectAsync(cts.Token));

        if (ex is SocketException) return; // this machine has no route at all: nothing to cancel
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void WebSocket_endpoints_can_use_tls_and_a_path()
    {
        Assert.Equal("wss://game.example.com/game", new TransportEndpointOptions { Host = "game.example.com", Port = 443, Secure = true, Path = "/game" }.WebSocketUri().ToString());
        Assert.Equal("ws://127.0.0.1:5000/game", new TransportEndpointOptions { Port = 5000, Path = "game" }.WebSocketUri().ToString());
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A one-connection WebSocket server: hands the accepted socket to the test.</summary>
    private static async Task<(HttpListener Listener, Uri Url, Task<WebSocket> Accepted)> WsServer()
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var accepted = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            return (WebSocket)(await ctx.AcceptWebSocketAsync(null)).WebSocket;
        });
        await Task.Yield();
        return (listener, new Uri($"ws://localhost:{port}/"), accepted);
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReadMessage(WebSocket ws)
    {
        var buf = new byte[4096];
        var result = await ws.ReceiveAsync(buf, CancellationToken.None);
        return (result.MessageType, buf[..result.Count]);
    }

    [Fact]
    public async Task WebSocket_client_sends_frames_the_server_parses_and_survives_receive_timeouts()
    {
        var (listener, url, accepted) = await WsServer();
        using var _ = listener;
        await using var client = new AltruistWebSocketClient(url, new MessagePackClientCodec());
        var disconnected = 0;
        client.OnDisconnected += () => Interlocked.Increment(ref disconnected);
        await client.ConnectAsync();
        var server = await accepted;

        await client.SendAsync("score", new ScorePacket { Score = 3 });
        var (type, data) = await ReadMessage(server);
        Assert.Equal(WebSocketMessageType.Binary, type);
        Assert.Equal(5, data[0]);
        Assert.Equal("score", Encoding.UTF8.GetString(data, 1, 5));
        Assert.Equal(3, MessagePackSerializer.Deserialize<ScorePacket>(data.AsMemory(6)).Score);

        Assert.Null(await client.ReceiveAsync(TimeSpan.FromMilliseconds(50)));
        Assert.True(client.IsConnected);
        await server.SendAsync(new byte[] { 9 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, await client.ReceiveAsync(TimeSpan.FromSeconds(5)));

        await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Assert.Null(await client.ReceiveAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task WebSocket_json_codec_sends_event_and_data_text_frames_whatever_the_provider_case()
    {
        var (listener, url, accepted) = await WsServer();
        using var _ = listener;
        await using var client = new AltruistWebSocketClient(url, new UpperCaseJson());
        await client.ConnectAsync();
        var server = await accepted;

        await client.SendAsync("score", new ScorePacket { Score = 4 });
        var (type, data) = await ReadMessage(server);

        Assert.Equal(WebSocketMessageType.Text, type);
        using var doc = System.Text.Json.JsonDocument.Parse(data);
        Assert.Equal("score", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("data").GetProperty("Score").GetInt32());
    }

    private sealed class UpperCaseJson : IClientCodec
    {
        private readonly JsonClientCodec _inner = new();
        public string Provider => "JSON";
        public byte[] Serialize<T>(T value) => _inner.Serialize(value);
        public T? Deserialize<T>(byte[] data) => _inner.Deserialize<T>(data);
        public T? Deserialize<T>(ReadOnlySpan<byte> data) => _inner.Deserialize<T>(data);
    }
}
