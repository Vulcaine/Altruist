using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Altruist;
using Altruist.Client;
using MessagePack;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Verifies <see cref="AltruistClientRouter"/> correctly routes
/// <see cref="IAltruistClientRouter.SendAsync"/> to the configured
/// <c>defaultTransport</c>, exposes per-transport explicit senders,
/// and throws cleanly when a requested transport isn't configured.
/// </summary>
public sealed class RouterSendAsyncTests
{
    [MessagePackObject]
    public sealed class Ping : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 9100;
        [Key(1)] public int Seq { get; set; }
    }

    private static ClientCodecResolver BuildCodecResolver() =>
        new(new IClientCodec[] { new MessagePackClientCodec(), new JsonClientCodec() });

    private static (TcpListener listener, int port, Task<byte[]> firstFrame) StartCapturingTcpServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var firstFrame = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            var stream = conn.GetStream();

            // Server-side handshake: write client id
            var idBytes = Encoding.UTF8.GetBytes("server-assigned-id");
            var idHdr = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idHdr, idBytes.Length);
            await stream.WriteAsync(idHdr);
            await stream.WriteAsync(idBytes);
            await stream.FlushAsync();

            // Read one inbound frame
            var hdr = new byte[4];
            await stream.ReadExactlyAsync(hdr);
            int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
            var body = new byte[len];
            await stream.ReadExactlyAsync(body);
            return body;
        });

        return (listener, port, firstFrame);
    }

    [Fact]
    public async Task SendAsync_UsesDefaultTransport_FromConfig()
    {
        var (listener, port, firstFrame) = StartCapturingTcpServer();

        var config = new ClientTransportConfig
        {
            DefaultTransport = "tcp",
            Tcp = new TransportEndpointOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Codec = new CodecOptions { Provider = "messagepack" },
            }
        };

        var dispatcher = new ClientPacketDispatcher();
        await using var router = new AltruistClientRouter(config, BuildCodecResolver(), dispatcher);

        try
        {
            await router.ConnectAsync();
            await router.SendAsync("ping", new Ping { Seq = 42 });

            var frame = await firstFrame.WaitAsync(TimeSpan.FromSeconds(2));

            // First byte = gate name length = 4 ("ping")
            Assert.Equal(4, frame[0]);
            Assert.Equal("ping", Encoding.UTF8.GetString(frame, 1, 4));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpExplicit_SendsViaTcpTransport_EvenWhenDefaultIsDifferent()
    {
        var (listener, port, firstFrame) = StartCapturingTcpServer();

        var config = new ClientTransportConfig
        {
            // default is udp, but Udp endpoint isn't actually configured —
            // proves that .Tcp.SendAsync ignores DefaultTransport entirely.
            DefaultTransport = "udp",
            Tcp = new TransportEndpointOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Codec = new CodecOptions { Provider = "messagepack" },
            }
        };

        var dispatcher = new ClientPacketDispatcher();
        await using var router = new AltruistClientRouter(config, BuildCodecResolver(), dispatcher);

        try
        {
            await router.ConnectAsync();
            await router.Tcp.SendAsync("explicit", new Ping { Seq = 1 });

            var frame = await firstFrame.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(8, frame[0]);
            Assert.Equal("explicit", Encoding.UTF8.GetString(frame, 1, 8));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Tcp_ThrowsCleanly_WhenNotConfigured()
    {
        var config = new ClientTransportConfig
        {
            DefaultTransport = "ws",
            Ws = new TransportEndpointOptions { Host = "127.0.0.1", Port = 1234 },
        };
        var router = new AltruistClientRouter(config, BuildCodecResolver(), new ClientPacketDispatcher());

        var ex = Assert.Throws<InvalidOperationException>(() => _ = router.Tcp);
        Assert.Contains("TCP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Udp_ThrowsCleanly_WhenNotConfigured()
    {
        var config = new ClientTransportConfig
        {
            Tcp = new TransportEndpointOptions { Host = "127.0.0.1", Port = 1234 },
        };
        var router = new AltruistClientRouter(config, BuildCodecResolver(), new ClientPacketDispatcher());

        Assert.Throws<InvalidOperationException>(() => _ = router.Udp);
    }

    [Fact]
    public void Ws_ThrowsCleanly_WhenNotConfigured()
    {
        var config = new ClientTransportConfig
        {
            Tcp = new TransportEndpointOptions { Host = "127.0.0.1", Port = 1234 },
        };
        var router = new AltruistClientRouter(config, BuildCodecResolver(), new ClientPacketDispatcher());

        Assert.Throws<InvalidOperationException>(() => _ = router.Ws);
    }

    [Fact]
    public void SendAsync_Throws_WhenDefaultTransportNotConfigured()
    {
        var config = new ClientTransportConfig
        {
            DefaultTransport = "udp",
            // Only TCP configured — defaultTransport=udp can't resolve a sender.
            Tcp = new TransportEndpointOptions { Host = "127.0.0.1", Port = 1234 },
        };
        var router = new AltruistClientRouter(config, BuildCodecResolver(), new ClientPacketDispatcher());

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => router.SendAsync("g", new Ping { Seq = 0 })).Result;
        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
