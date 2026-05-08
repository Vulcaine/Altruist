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
/// Asserts <see cref="AltruistTcpClient.SendAsync{T}"/> writes the bytes the
/// server's <c>ConnectionManager</c> expects: <c>[4 LE length][1 byte gateLen]
/// [gate UTF-8][codec(payload)]</c>.
/// </summary>
public sealed class SendFramingTests
{
    [MessagePackObject]
    public sealed class CLogin : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 100;
        [Key(1)] public string Username { get; set; } = "";
    }

    [Fact]
    public async Task SendAsync_WritesLengthPrefixed_GatePrefixedFrame()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverAccept = listener.AcceptTcpClientAsync();
        var client = new AltruistTcpClient(
            new EndpointConfig("127.0.0.1", port),
            new MessagePackClientCodec());

        byte[]? receivedHeader = null;
        byte[]? receivedFrame = null;

        var serverTask = Task.Run(async () =>
        {
            using var serverConn = await serverAccept;
            var stream = serverConn.GetStream();

            // Server first writes the handshake clientId
            var idBytes = Encoding.UTF8.GetBytes("server-id");
            var idHeader = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idHeader, idBytes.Length);
            await stream.WriteAsync(idHeader);
            await stream.WriteAsync(idBytes);
            await stream.FlushAsync();

            // Now read what the client sends
            var hdr = new byte[4];
            await stream.ReadExactlyAsync(hdr);
            receivedHeader = hdr;
            int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);

            var body = new byte[len];
            await stream.ReadExactlyAsync(body);
            receivedFrame = body;
        });

        try
        {
            await client.ConnectAsync();
            await client.SendAsync("login", new CLogin { Username = "alice" });

            // Wait for server to record the bytes
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(receivedFrame);
            // First byte = gate name length
            int gateLen = receivedFrame![0];
            var gate = Encoding.UTF8.GetString(receivedFrame, 1, gateLen);
            Assert.Equal("login", gate);

            // Remainder = MessagePack-encoded CLogin
            var payloadBytes = receivedFrame.AsSpan(1 + gateLen).ToArray();
            var decoded = new MessagePackClientCodec().Deserialize<CLogin>(payloadBytes);
            Assert.NotNull(decoded);
            Assert.Equal("alice", decoded!.Username);
        }
        finally
        {
            client.Dispose();
            listener.Stop();
        }
    }
}
