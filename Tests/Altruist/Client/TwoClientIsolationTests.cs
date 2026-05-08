using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Altruist.Client;
using Xunit;

namespace Tests.Altruist.Client;

/// <summary>
/// Two clients connect to one listener; each must end up with a distinct
/// <see cref="AltruistTcpClient.ClientId"/> and a per-instance
/// <see cref="AltruistTcpClient.IncomingQueue"/> — no cross-contamination.
/// </summary>
public sealed class TwoClientIsolationTests
{
    [Fact]
    public async Task TwoClients_GetDistinctClientIds_AndSeparateQueues()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverDone = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var conn = await listener.AcceptTcpClientAsync();
                var stream = conn.GetStream();

                var id = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());
                var hdr = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(hdr, id.Length);
                await stream.WriteAsync(hdr);
                await stream.WriteAsync(id);
                await stream.FlushAsync();

                // Send one distinct frame per client so we can verify queues are separate.
                var bodyText = Encoding.UTF8.GetBytes($"hello-{i}");
                var bodyHdr = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bodyHdr, bodyText.Length);
                await stream.WriteAsync(bodyHdr);
                await stream.WriteAsync(bodyText);
                await stream.FlushAsync();
                await Task.Delay(200);
                conn.Close();
            }
            serverDone.SetResult();
        });

        var c1 = new AltruistTcpClient(new EndpointConfig("127.0.0.1", port), new MessagePackClientCodec());
        var c2 = new AltruistTcpClient(new EndpointConfig("127.0.0.1", port), new MessagePackClientCodec());
        try
        {
            await c1.ConnectAsync();
            await c2.ConnectAsync();

            Assert.NotEqual(c1.ClientId, c2.ClientId);
            Assert.NotEmpty(c1.ClientId);
            Assert.NotEmpty(c2.ClientId);

            c1.StartReadLoop();
            c2.StartReadLoop();

            await Task.Delay(400);

            Assert.True(c1.IncomingQueue.TryDequeue(out var p1));
            Assert.True(c2.IncomingQueue.TryDequeue(out var p2));
            Assert.NotEqual(Encoding.UTF8.GetString(p1!), Encoding.UTF8.GetString(p2!));

            await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            c1.Dispose();
            c2.Dispose();
            listener.Stop();
        }
    }
}
