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
/// await _ws.ConnectAsync("/"); // the [Portal("/")] path
/// await _ws.SendAsync("chat", new CChat { Message = "hi" });
/// var msg = await _ws.ReceiveAsync(TimeSpan.FromSeconds(1));
/// </code>
///
/// <para>WebSockets multiplex over the HTTP listener, so the client connects to the HTTP host and port, at the
/// path of the portal under test (portal paths match exactly).</para>
/// </summary>
public sealed class TestWebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly string _origin;
    private readonly ClientWebSocket _ws = new();
    private readonly WebSocketMessageType _messageType;

    /// <summary>Codec selected from <c>altruist:server:transport:websocket:codec:provider</c> (or the global provider); JSON uses text frames, MessagePack binary frames.</summary>
    public ITestCodec Codec { get; }
    /// <summary>The socket is open.</summary>
    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>An unconnected plain (no TLS) WebSocket client for <c>{http host}:{http port}</c>.</summary>
    /// <param name="cfg">The test configuration.</param>
    public TestWebSocketClient(IConfiguration cfg)
    {
        var host = TestHttpClient.NormalizeHost(cfg["altruist:server:http:host"] ?? "localhost");
        var port = cfg["altruist:server:http:port"] ?? "8080";
        _origin = $"ws://{host}:{port}"; // nosemgrep: javascript.lang.security.detect-insecure-websocket.detect-insecure-websocket -- the local test server

        Codec = TestCodecResolver.Resolve(cfg, "websocket");
        // JSON over WS is text-frame, MessagePack is binary-frame.
        _messageType = Codec.Provider == "json" ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
    }

    /// <summary>Opens the WebSocket to a portal.</summary>
    /// <param name="portalPath">The portal's <see cref="PortalAttribute"/> path, such as <c>/</c> or <c>/game</c>.</param>
    /// <param name="ct">Cancels the handshake.</param>
    /// <exception cref="ArgumentException"><paramref name="portalPath"/> does not start with <c>/</c>.</exception>
    public Task ConnectAsync(string portalPath, CancellationToken ct = default)
    {
        if (!portalPath.StartsWith('/'))
            throw new ArgumentException($"A portal path starts with '/': \"{portalPath}\".", nameof(portalPath));
        return _ws.ConnectAsync(new Uri(_origin + portalPath), ct);
    }

    /// <summary>Send a single Altruist envelope: <c>{ event, payload }</c> encoded
    /// via the configured codec. The server's portal router uses the event field
    /// to dispatch.</summary>
    /// <param name="eventName">The event (gate) name.</param>
    /// <param name="payload">The payload, or null.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
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
    /// Returns <c>null</c> on timeout, on a close frame or when not connected. Note that a timed-out
    /// receive aborts a <see cref="ClientWebSocket"/>: the client is unusable afterwards.</summary>
    /// <param name="timeout">How long to wait for a complete message.</param>
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
    /// <param name="timeout">How long to wait for a complete message.</param>
    public async Task<string?> ReceiveTextAsync(TimeSpan timeout)
    {
        var bytes = await ReceiveAsync(timeout);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Closes the socket gracefully when open, then disposes it.</summary>
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

    /// <summary>Disposes the socket without a close handshake.</summary>
    public void Dispose() => _ws.Dispose();
}
