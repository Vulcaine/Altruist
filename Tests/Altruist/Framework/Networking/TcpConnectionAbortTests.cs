using System.Net;
using System.Net.Sockets;
using Altruist.Socket;
using Xunit;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// The outbound queues abort a client that stopped reading; on TCP that abort has to tear the real
/// socket down, or the client stays connected while every later packet is dropped.
/// </summary>
public sealed class TcpConnectionAbortTests
{
    [Fact]
    public async Task Abort_fails_a_send_stuck_on_a_peer_that_stopped_reading_and_resets_the_peer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var connection = new TcpConnection(accepted, "slow-peer", authDetails: null, lengthPrefixed: true);

            var stuckSend = FillUntilASendBlocksAsync(connection);
            await Task.Delay(200);
            Assert.False(stuckSend.IsCompleted, "the peer never reads, so a send must block once the buffers are full");

            connection.Abort();

            await Assert.ThrowsAnyAsync<Exception>(() => stuckSend.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(connection.IsConnected);
            var peerRead = Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                var buffer = new byte[64 * 1024];
                var stream = peer.GetStream();
                while (await stream.ReadAsync(buffer) > 0) { }
            });
            await peerRead.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Abort_on_a_closed_connection_does_nothing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var accepted = await listener.AcceptTcpClientAsync();
            var connection = new TcpConnection(accepted, "closed-peer", authDetails: null, lengthPrefixed: true);
            await connection.CloseAsync();

            connection.Abort();

            Assert.False(connection.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task FillUntilASendBlocksAsync(TcpConnection connection)
    {
        var chunk = new byte[64 * 1024];
        while (connection.IsConnected)
            await connection.SendAsync(chunk);
    }
}
