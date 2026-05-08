/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// WebSocket client pre-configured for the Altruist test server. Default config
/// ships <c>altruist:server:transport:websocket:codec:provider: json</c>, so
/// payloads are JSON-encoded; switch to <c>messagepack</c> in config-test.yml
/// and the client follows.
///
/// <code>
/// public ChatTest(TestWebSocketClient ws) { _ws = ws; }
///
/// await _ws.ConnectAsync();
/// await _ws.SendAsync("chat", new CChat { Message = "hi" });
/// var msg = await _ws.ReceiveAsync(TimeSpan.FromSeconds(1));
/// </code>
///
/// <para>WebSockets multiplex over the HTTP listener, so endpoint resolution
/// reuses HTTP host/port plus the configured ws path
/// (<c>altruist:server:transport:websocket:path</c>, default <c>/ws</c>).</para>
/// </summary>
public sealed class TestWebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly Uri _url;
    private readonly ClientWebSocket _ws = new();
    private readonly WebSocketMessageType _messageType;

    public ITestCodec Codec { get; }
    public bool IsConnected => _ws.State == WebSocketState.Open;

    public TestWebSocketClient(IConfiguration cfg)
    {
        var host = TestHttpClient.NormalizeHost(cfg["altruist:server:http:host"] ?? "localhost");
        var port = cfg["altruist:server:http:port"] ?? "8080";
        var path = cfg["altruist:server:transport:websocket:path"] ?? "/ws";
        if (!path.StartsWith("/")) path = "/" + path;
        _url = new Uri($"ws://{host}:{port}{path}");

        Codec = TestCodecResolver.Resolve(cfg, "websocket");
        // JSON over WS is text-frame, MessagePack is binary-frame.
        _messageType = Codec.Provider == "json" ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _ws.ConnectAsync(_url, ct);

    /// <summary>Send a single Altruist envelope: <c>{ event, payload }</c> encoded
    /// via the configured codec. The server's portal router uses the event field
    /// to dispatch.</summary>
    public async Task SendAsync(string eventName, object? payload = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException(
            "TestWebSocketClient not connected. Call ConnectAsync first.");

        // JSON path: { "event": "...", "payload": {...} } — matches Altruist's WS envelope.
        // MsgPack path: same shape, codec-encoded.
        var envelope = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["payload"] = payload,
        };
        var bytes = Codec.Serialize(envelope);
        await _ws.SendAsync(bytes, _messageType, endOfMessage: true, ct);
    }

    /// <summary>Receive one frame within <paramref name="timeout"/>. Returns the
    /// raw codec bytes; pass to <see cref="Codec"/> to decode into a known shape.
    /// Returns <c>null</c> on timeout.</summary>
    public async Task<byte[]?> ReceiveAsync(TimeSpan timeout)
    {
        if (!IsConnected) return null;

        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[16384];
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buf, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            return ms.ToArray();
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Convenience: receive and decode UTF-8 text. Useful for JSON
    /// transports when you just want to inspect the raw envelope.</summary>
    public async Task<string?> ReceiveTextAsync(TimeSpan timeout)
    {
        var bytes = await ReceiveAsync(timeout);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
        }
        catch { }
        Dispose();
    }

    public void Dispose() => _ws.Dispose();
}
