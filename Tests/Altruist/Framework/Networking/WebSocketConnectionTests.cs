/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.WebSockets;

using Altruist.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tests.Altruist.Framework.Networking;

/// <summary>
/// WebSocketConnection regressions over a real Kestrel socket (ephemeral port) with a
/// ClientWebSocket peer: concurrent sends (only one outstanding send is allowed per socket),
/// messages larger than one receive buffer / split into several frames, the size cap, the
/// close handshake and Abort.
/// </summary>
public sealed class WebSocketConnectionTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _uri = null!;
    private readonly TaskCompletionSource<WebSocketConnection> _serverSide = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientWebSocket _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/ws", async (HttpContext ctx) =>
        {
            var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            _serverSide.TrySetResult(new WebSocketConnection(socket, "/ws", "127.0.0.1", Guid.NewGuid().ToString("N"), null));
            await _release.Task; // keep the request (and socket) alive for the test
        });
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _uri = new Uri(address.Replace("http://", "ws://") + "/ws");
        _client = new ClientWebSocket();
        await _client.ConnectAsync(_uri, Timeout(5));
    }

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static CancellationToken Timeout(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    private Task<WebSocketConnection> ServerAsync() => _serverSide.Task.WaitAsync(TimeSpan.FromSeconds(5));

    private async Task<byte[]> ClientReceiveAsync()
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult r;
        do
        {
            r = await _client.ReceiveAsync(buffer, Timeout(10));
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return ms.ToArray();
    }

    private static byte[] Pattern(int length, int seed)
    {
        var b = new byte[length];
        for (var i = 0; i < b.Length; i++)
            b[i] = (byte)(i * 31 + seed);
        return b;
    }

    [Fact]
    public async Task Concurrent_sends_from_many_threads_are_serialized()
    {
        var server = await ServerAsync();
        const int messages = 64;

        // Without the send lock the second overlapping SendAsync throws
        // "There is already one outstanding 'SendAsync' call for this WebSocket instance".
        var sends = Enumerable.Range(0, messages).Select(i => Task.Run(() => i % 2 == 0
            ? server.SendAsync(Pattern(32 * 1024, i))
            : server.SendAsync(new ReadOnlyMemory<byte>(Pattern(32 * 1024, i))))).ToArray();

        var received = new List<byte[]>();
        for (var i = 0; i < messages; i++)
            received.Add(await ClientReceiveAsync());
        await Task.WhenAll(sends);

        // Every message arrives whole (no interleaved frames) and each payload exactly once.
        var expected = Enumerable.Range(0, messages).Select(i => Convert.ToBase64String(Pattern(32 * 1024, i))).OrderBy(x => x);
        Assert.Equal(expected, received.Select(Convert.ToBase64String).OrderBy(x => x));
    }

    [Fact]
    public async Task A_message_larger_than_the_receive_buffer_is_returned_whole()
    {
        var server = await ServerAsync();
        var payload = Pattern(20_000, 3); // > the 4 KB receive buffer

        await _client.SendAsync(payload, WebSocketMessageType.Binary, true, Timeout(5));

        // Before the fix only the first 4096 bytes came back and the rest was read as new messages.
        Assert.Equal(payload, await server.ReceiveAsync(Timeout(5)));
    }

    [Fact]
    public async Task A_message_split_into_several_frames_is_reassembled()
    {
        var server = await ServerAsync();
        var parts = new[] { Pattern(100, 1), Pattern(5000, 2), Pattern(7, 3) };

        for (var i = 0; i < parts.Length; i++)
            await _client.SendAsync(parts[i], WebSocketMessageType.Binary, endOfMessage: i == parts.Length - 1, Timeout(5));
        await _client.SendAsync(Pattern(10, 9), WebSocketMessageType.Binary, true, Timeout(5));

        Assert.Equal(parts.SelectMany(p => p).ToArray(), await server.ReceiveAsync(Timeout(5)));
        Assert.Equal(Pattern(10, 9), await server.ReceiveAsync(Timeout(5))); // the next message is separate
    }

    [Fact]
    public async Task A_message_over_the_size_cap_closes_the_connection()
    {
        var server = await ServerAsync();
        var chunk = new byte[64 * 1024];

        // The client keeps reading (like a real client's read loop) so it can answer the server's
        // close handshake; the server's CloseAsync waits for that answer.
        var clientSide = Task.Run(async () =>
        {
            var r = await _client.ReceiveAsync(new byte[16], Timeout(10));
            if (r.MessageType == WebSocketMessageType.Close)
                await _client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Timeout(5));
            return r.MessageType;
        });
        var sender = Task.Run(async () =>
        {
            try
            {
                for (var sent = 0; sent <= WebSocketConnection.MaxMessageBytes + chunk.Length; sent += chunk.Length)
                    await _client.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: false, Timeout(10));
            }
            catch (WebSocketException) { /* server closed on us */ }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { /* socket no longer open */ }
        });

        var result = await server.ReceiveAsync(Timeout(10)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(result);
        Assert.False(server.IsConnected);

        Assert.Equal(WebSocketMessageType.Close, await clientSide.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, _client.CloseStatus);
        await sender.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_oversized_message_from_a_peer_that_never_reads_does_not_hang_the_read_loop()
    {
        var server = await ServerAsync();
        var chunk = new byte[64 * 1024];
        // The peer only sends: it never reads, so it never answers a close handshake.
        var sender = Task.Run(async () =>
        {
            try
            {
                for (var sent = 0; sent <= WebSocketConnection.MaxMessageBytes + chunk.Length; sent += chunk.Length)
                    await _client.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: false, Timeout(10));
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        });

        var started = DateTime.UtcNow;
        var result = await server.ReceiveAsync(Timeout(30)).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty(result);
        Assert.False(server.IsConnected);
        Assert.True(DateTime.UtcNow - started < WebSocketConnection.CloseTimeout + TimeSpan.FromSeconds(5));
        await sender.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_server_close_to_a_peer_that_never_answers_gives_up_after_the_timeout()
    {
        var server = await ServerAsync();
        var started = DateTime.UtcNow;
        // The client never reads, so the close handshake can't complete: the server must not wait forever.
        await server.CloseAsync().WaitAsync(WebSocketConnection.CloseTimeout + TimeSpan.FromSeconds(5));
        Assert.False(server.IsConnected);
        Assert.True(DateTime.UtcNow - started >= WebSocketConnection.CloseTimeout - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_peer_close_is_acknowledged_so_the_peer_sees_a_clean_close()
    {
        var server = await ServerAsync();
        var receive = server.ReceiveAsync(Timeout(10));

        // The client's CloseAsync waits for the server's close frame. Before the fix the server
        // never answered (CloseAsync was a no-op in CloseReceived) and the client timed out.
        await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Timeout(5));

        Assert.Empty(await receive);
        Assert.Equal(WebSocketState.Closed, _client.State);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, _client.CloseStatus);
    }

    [Fact]
    public async Task A_peer_close_in_the_middle_of_a_fragmented_message_is_acknowledged()
    {
        var server = await ServerAsync();
        var receive = server.ReceiveAsync(Timeout(10));

        await _client.SendAsync(Pattern(10, 1), WebSocketMessageType.Binary, endOfMessage: false, Timeout(5));
        await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Timeout(5));

        Assert.Empty(await receive);
        Assert.Equal(WebSocketState.Closed, _client.State);
    }

    [Fact]
    public async Task Abort_tears_the_connection_down_immediately()
    {
        var server = await ServerAsync();

        server.Abort();

        Assert.False(server.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var r = await _client.ReceiveAsync(new byte[16], Timeout(5));
            // Some stacks surface the abort as an immediate close instead of an exception.
            if (r.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("closed");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(new byte[] { 1 }));
    }
}
