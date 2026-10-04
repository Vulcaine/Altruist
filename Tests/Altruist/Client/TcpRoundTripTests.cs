using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Altruist;
using Altruist.Client;
using MessagePack;
using MessagePack.Resolvers;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// End-to-end round-trip on a real loopback TCP socket. Server side emits a
/// MessagePack-encoded <see cref="MessageEnvelope"/> wrapping a sample packet;
/// the client reads it via <see cref="AltruistTcpClient"/> and dispatches via
/// <see cref="ClientPacketDispatcher"/> to a <see cref="PacketHandlerAttribute"/>-
/// marked test handler.
///
/// <para>This is the contract test that locks the package to the actual server
/// wire shape — if anyone changes either side, this fails.</para>
/// </summary>
public sealed class TcpRoundTripTests
{
    [MessagePackObject]
    public sealed class TestPacket : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 1252;
        [Key(1)] public uint VID { get; set; }
        [Key(2)] public int Damage { get; set; }
    }

    [PacketHandler]
    public sealed class TestHandler
    {
        public TestPacket? Received { get; private set; }

        [Packet(typeof(TestPacket))]
        public void OnTestPacket(TestPacket pkt) => Received = pkt;
    }

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    [Fact]
    public async Task ServerEnvelope_ReachesAttributeHandler_WithMatchingPayload()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverAccept = listener.AcceptTcpClientAsync();
        var codec = new MessagePackClientCodec();
        var client = new AltruistTcpClient(new EndpointConfig("127.0.0.1", port), codec);

        // Server side: write the handshake clientId, then a framed envelope.
        var serverConnTask = Task.Run(async () =>
        {
            using var serverConn = await serverAccept;
            var serverStream = serverConn.GetStream();

            // Handshake: [4 LE length][UTF-8 clientId]
            var clientId = "test-client-id";
            var idBytes = Encoding.UTF8.GetBytes(clientId);
            var idHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idHeader, idBytes.Length);
            await serverStream.WriteAsync(idHeader);
            await serverStream.WriteAsync(idBytes);
            await serverStream.FlushAsync();

            // Envelope: MessagePackEncode(MessageEnvelope(TestPacket))
            var pkt = new TestPacket { VID = 12345, Damage = 87 };
            var envelope = new MessageEnvelope(pkt, clientId);
            var encoded = MessagePackSerializer.Serialize(envelope, ServerOptions);

            var frameHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(frameHeader, encoded.Length);
            await serverStream.WriteAsync(frameHeader);
            await serverStream.WriteAsync(encoded);
            await serverStream.FlushAsync();

            // Stay open long enough for the client to read
            await Task.Delay(500);
        });

        try
        {
            await client.ConnectAsync();
            Assert.Equal("test-client-id", client.ClientId);

            var handler = new TestHandler();
            var dispatcher = new ClientPacketDispatcher();
            dispatcher.Register(handler);

            client.StartReadLoop();

            // Wait briefly for the frame to arrive + dispatch
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (handler.Received is null && DateTime.UtcNow < deadline)
            {
                while (client.IncomingQueue.TryDequeue(out var p))
                    dispatcher.Dispatch(p, codec);
                await Task.Delay(20);
            }

            Assert.NotNull(handler.Received);
            Assert.Equal((uint)1252, handler.Received!.MessageCode);
            Assert.Equal((uint)12345, handler.Received.VID);
            Assert.Equal(87, handler.Received.Damage);
        }
        finally
        {
            client.Dispose();
            listener.Stop();
            await serverConnTask;
        }
    }
}
