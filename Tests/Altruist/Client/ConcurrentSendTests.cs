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
/// Sends N packets concurrently from N threads. Asserts that the receiver reads
/// exactly N intact, non-interleaved frames. The package's <see cref="AltruistTcpClient.SendAsync{T}"/>
/// MUST take a stream lock around write+flush — otherwise threads overlap and the
/// server's length-prefix framer mis-aligns. Production catches this as random
/// packet loss; the test catches it deterministically.
/// </summary>
public sealed class ConcurrentSendTests
{
    [MessagePackObject]
    public sealed class Numbered : IPacketBase
    {
        [Key(0)] public uint MessageCode { get; set; } = 200;
        [Key(1)] public int N { get; set; }
    }

    [Fact]
    public async Task FiftyConcurrentSends_AllArriveIntactAndInOrderlessOrder()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const int hitCount = 50;
        var receivedNs = new List<int>(hitCount);
        var serverAccept = listener.AcceptTcpClientAsync();

        var serverTask = Task.Run(async () =>
        {
            using var serverConn = await serverAccept;
            var stream = serverConn.GetStream();

            // Handshake
            var id = Encoding.UTF8.GetBytes("c");
            var idHdr = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(idHdr, id.Length);
            await stream.WriteAsync(idHdr);
            await stream.WriteAsync(id);
            await stream.FlushAsync();

            for (int i = 0; i < hitCount; i++)
            {
                var hdr = new byte[4];
                await stream.ReadExactlyAsync(hdr);
                int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);

                Assert.InRange(len, 1, 1024);  // sanity — frames shouldn't be huge
                var body = new byte[len];
                await stream.ReadExactlyAsync(body);

                int gateLen = body[0];
                var payload = body.AsSpan(1 + gateLen).ToArray();
                var n = new MessagePackClientCodec().Deserialize<Numbered>(payload);
                Assert.NotNull(n);
                lock (receivedNs) receivedNs.Add(n!.N);
            }
        });

        var client = new AltruistTcpClient(
            new EndpointConfig("127.0.0.1", port),
            new MessagePackClientCodec());
        try
        {
            await client.ConnectAsync();

            var sends = new Task[hitCount];
            for (int i = 0; i < hitCount; i++)
            {
                int captured = i;
                sends[i] = Task.Run(() => client.SendAsync("send", new Numbered { N = captured }));
            }
            await Task.WhenAll(sends);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(hitCount, receivedNs.Count);
            // Every N must appear exactly once (order-irrelevant)
            var expected = Enumerable.Range(0, hitCount).ToHashSet();
            Assert.Equal(expected, receivedNs.ToHashSet());
        }
        finally
        {
            client.Dispose();
            listener.Stop();
        }
    }
}
