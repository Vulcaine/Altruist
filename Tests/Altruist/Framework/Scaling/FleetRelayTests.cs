/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Altruist;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tests.Altruist.Framework.Scaling;

/// <summary>
/// Two real servers on loopback: a client reaching server b through server a (<c>?node=b</c>)
/// talks to b, and b sees the client as a saw it; forged relay headers are not trusted.
/// </summary>
public sealed class FleetRelayTests : IAsyncLifetime
{
    private const string Secret = "relay-test-secret";
    private readonly InMemoryFleetBackplane _backplane = new(shared: true);
    private readonly List<WebApplication> _apps = new();
    private (WebApplication App, int Port, Fleet Fleet) _a;
    private (WebApplication App, int Port, Fleet Fleet) _b;

    public async Task InitializeAsync()
    {
        _a = await StartNode("a", Secret);
        _b = await StartNode("b", Secret);
        await _a.Fleet.HeartbeatAsync();
        await _b.Fleet.HeartbeatAsync();
        await _a.Fleet.HeartbeatAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try
            { await app.StopAsync(); }
            catch (Exception) { }
            await app.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<(WebApplication App, int Port, Fleet Fleet)> StartNode(string id, string? secret, string cluster = "relay")
    {
        var port = FreePort();
        var node = new ServerNode(nodeId: id);
        var fleet = new Fleet(_backplane, node, new FleetOptions { Cluster = cluster, InternalAddress = $"127.0.0.1:{port}" });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["altruist:server:fleet:secret"] = secret });
        builder.Services.AddSingleton<IFleet>(fleet);
        var app = builder.Build();
        app.UseWebSockets();
        app.UseMiddleware<FleetRelayMiddleware>();
        app.Run(async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                // A plain request shows what the middleware made of it.
                await ctx.Response.WriteAsync(Describe(ctx, id));
                return;
            }
            var protocol = ctx.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(protocol);
            await ws.SendAsync(Encoding.UTF8.GetBytes(Describe(ctx, id)), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[256 * 1024];
            while (true)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                var total = r.Count;
                while (!r.EndOfMessage)
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
                    total += r.Count;
                }
                var text = r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, total) : null;
                if (text == "close-4001")
                {
                    await ws.CloseOutputAsync((WebSocketCloseStatus)4001, "server says bye", CancellationToken.None);
                    return;
                }
                await ws.SendAsync(buffer.AsMemory(0, total), r.MessageType, true, CancellationToken.None);
            }
        });
        await app.StartAsync();
        _apps.Add(app);
        return (app, port, fleet);
    }

    private static string Describe(HttpContext ctx, string id) => JsonSerializer.Serialize(new
    {
        node = id,
        ip = ctx.Connection.RemoteIpAddress?.ToString(),
        relayed = ctx.Items.ContainsKey(FleetRelayMiddleware.RelayedItem),
        host = ctx.Request.Host.Value,
        scheme = ctx.Request.Scheme,
        query = ctx.Request.QueryString.Value,
        auth = ctx.Request.Headers.Authorization.ToString(),
        leaked = ctx.Request.Headers.Keys.Any(k => k.StartsWith("X-Altruist-Relay", StringComparison.OrdinalIgnoreCase)),
    });

    private static async Task<(ClientWebSocket Ws, JsonElement Hello)> Open(int port, string query = "", Action<ClientWebSocketOptions>? options = null)
    {
        var ws = new ClientWebSocket();
        options?.Invoke(ws.Options);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/game{query}"), CancellationToken.None);
        var (_, text) = await ReceiveText(ws);
        return (ws, JsonDocument.Parse(text!).RootElement.Clone());
    }

    private static async Task<(WebSocketMessageType Type, string? Text, byte[] Bytes, WebSocketReceiveResult Last)> Receive(ClientWebSocket ws)
    {
        var buffer = new byte[256 * 1024];
        var total = 0;
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
            total += r.Count;
        } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
        var bytes = buffer[..total];
        return (r.MessageType, r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(bytes) : null, bytes, r);
    }

    private static async Task<(WebSocketMessageType Type, string? Text)> ReceiveText(ClientWebSocket ws)
    {
        var r = await Receive(ws);
        return (r.Type, r.Text);
    }

    [Fact]
    public async Task A_direct_connection_is_not_relayed()
    {
        var (ws, hello) = await Open(_b.Port);
        using (ws)
        {
            Assert.Equal("b", hello.GetProperty("node").GetString());
            Assert.False(hello.GetProperty("relayed").GetBoolean());
        }
    }

    [Fact]
    public async Task A_client_reaches_another_server_through_this_one_and_is_seen_as_itself()
    {
        var (ws, hello) = await Open(_a.Port, "?ticket=t1&node=b", o => o.SetRequestHeader("Authorization", "Bearer abc"));
        using (ws)
        {
            Assert.Equal("b", hello.GetProperty("node").GetString());
            Assert.True(hello.GetProperty("relayed").GetBoolean());
            Assert.Equal("127.0.0.1", hello.GetProperty("ip").GetString());
            Assert.Equal($"127.0.0.1:{_a.Port}", hello.GetProperty("host").GetString()); // the host the client asked for
            Assert.Equal("?ticket=t1&node=b", hello.GetProperty("query").GetString());
            Assert.Equal("Bearer abc", hello.GetProperty("auth").GetString());
            Assert.False(hello.GetProperty("leaked").GetBoolean());
        }
    }

    [Fact]
    public async Task Text_binary_and_large_messages_cross_the_relay_both_ways()
    {
        var (ws, _) = await Open(_a.Port, "?node=b");
        using (ws)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.Equal((WebSocketMessageType.Text, "hello"), await ReceiveText(ws));

            var big = new byte[100_000];
            new Random(7).NextBytes(big);
            // Sent in several frames; arrives as one message.
            await ws.SendAsync(big.AsMemory(0, 40_000), WebSocketMessageType.Binary, false, CancellationToken.None);
            await ws.SendAsync(big.AsMemory(40_000), WebSocketMessageType.Binary, true, CancellationToken.None);
            var (type, _, bytes, _) = await Receive(ws);
            Assert.Equal(WebSocketMessageType.Binary, type);
            Assert.Equal(big, bytes);
        }
    }

    [Fact]
    public async Task Closes_cross_the_relay_with_their_status()
    {
        var (ws, _) = await Open(_a.Port, "?node=b");
        await ws.SendAsync(Encoding.UTF8.GetBytes("close-4001"), WebSocketMessageType.Text, true, CancellationToken.None);
        var (_, _, _, last) = await Receive(ws);
        Assert.Equal(WebSocketMessageType.Close, last.MessageType);
        Assert.Equal((WebSocketCloseStatus)4001, last.CloseStatus);
        Assert.Equal("server says bye", last.CloseStatusDescription);
        ws.Dispose();

        var (ws2, _) = await Open(_a.Port, "?node=b");
        await ws2.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, ws2.State);
        ws2.Dispose();
    }

    [Theory]
    [InlineData("?node=a")]
    [InlineData("?node=nobody")]
    [InlineData("?node=")]
    [InlineData("")]
    public async Task This_server_or_an_unknown_one_is_served_here(string query)
    {
        var (ws, hello) = await Open(_a.Port, query);
        using (ws)
        {
            Assert.Equal("a", hello.GetProperty("node").GetString());
            Assert.False(hello.GetProperty("relayed").GetBoolean());
        }
    }

    [Fact]
    public async Task A_target_that_is_down_is_replaced_by_this_server()
    {
        await _b.App.StopAsync(); // still in a's view until its heartbeat expires
        var (ws, hello) = await Open(_a.Port, "?node=b");
        using (ws)
            Assert.Equal("a", hello.GetProperty("node").GetString());
    }

    [Fact]
    public async Task Without_a_secret_nothing_is_relayed_or_trusted()
    {
        var c = await StartNode("c", secret: null);
        await c.Fleet.HeartbeatAsync();
        var (ws, hello) = await Open(c.Port, "?node=b");
        using (ws)
            Assert.Equal("c", hello.GetProperty("node").GetString());
        var described = await Describe(c.Port, Signed("203.0.113.7", "http", "x", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal("127.0.0.1", described.GetProperty("ip").GetString());
    }

    // ------------------------------------------------------------------ forged headers

    private static Dictionary<string, string> Signed(string ip, string proto, string host, long unix, string secret = Secret)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("altruist-fleet-relay:" + secret));
        var payload = string.Create(CultureInfo.InvariantCulture, $"{ip}|{proto}|{host}|{unix}");
        return new()
        {
            [FleetRelayMiddleware.ForHeader] = ip,
            [FleetRelayMiddleware.ProtoHeader] = proto,
            [FleetRelayMiddleware.HostHeader] = host,
            [FleetRelayMiddleware.TimeHeader] = unix.ToString(CultureInfo.InvariantCulture),
            [FleetRelayMiddleware.SignatureHeader] = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload))),
        };
    }

    private static async Task<JsonElement> Describe(int port, Dictionary<string, string> headers)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/game");
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        using var res = await http.SendAsync(req);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Signed_relay_headers_restore_the_client()
    {
        var d = await Describe(_b.Port, Signed("203.0.113.7", "https", "play.example.com", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Equal("203.0.113.7", d.GetProperty("ip").GetString());
        Assert.Equal("https", d.GetProperty("scheme").GetString());
        Assert.Equal("play.example.com", d.GetProperty("host").GetString());
        Assert.True(d.GetProperty("relayed").GetBoolean());
        Assert.False(d.GetProperty("leaked").GetBoolean());
    }

    [Fact]
    public async Task Forged_stale_or_tampered_relay_headers_are_stripped_and_ignored()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var wrongKey = Signed("203.0.113.7", "http", "x", now, secret: "guess");
        var stale = Signed("203.0.113.7", "http", "x", now - 600);
        var tampered = Signed("203.0.113.7", "http", "x", now);
        tampered[FleetRelayMiddleware.ForHeader] = "198.51.100.1";
        var unsigned = new Dictionary<string, string> { [FleetRelayMiddleware.ForHeader] = "203.0.113.7" };
        foreach (var headers in new[] { wrongKey, stale, tampered, unsigned })
        {
            var d = await Describe(_b.Port, headers);
            Assert.Equal("127.0.0.1", d.GetProperty("ip").GetString());
            Assert.False(d.GetProperty("relayed").GetBoolean());
            Assert.False(d.GetProperty("leaked").GetBoolean());
        }
    }
}
