/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>
/// Lets a client reach a given server of the fleet through any of them, so the fleet needs one
/// public address and no sticky routing: a WebSocket request with <c>?node=&lt;id&gt;</c> (see
/// <see cref="IFleet.NodeParam"/>) for another live server is relayed to that server's internal
/// address, frame by frame, both ways. Requests for this server, for an unknown server, or that
/// the target refuses are served here as usual.
/// <para>
/// The relayed request carries the client's address in signed headers
/// (<c>altruist:server:fleet:secret</c>, else <c>altruist:security:key</c>); the receiving server
/// restores it as <see cref="ConnectionInfo.RemoteIpAddress"/> (and the original scheme and host),
/// so rate limits, bans and IP-bound tickets see the real client. Unsigned or stale relay headers
/// are ignored.
/// </para>
/// </summary>
public sealed class FleetRelayMiddleware
{
    /// <summary>Signed relay header: the client's IP address.</summary>
    public const string ForHeader = "X-Altruist-Relay-For";
    /// <summary>Signed relay header: the client's original scheme.</summary>
    public const string ProtoHeader = "X-Altruist-Relay-Proto";
    /// <summary>Signed relay header: the client's original host.</summary>
    public const string HostHeader = "X-Altruist-Relay-Host";
    /// <summary>Signed relay header: Unix time (seconds) of signing; accepted within 60 s of skew.</summary>
    public const string TimeHeader = "X-Altruist-Relay-Time";
    /// <summary>Base64 HMAC-SHA256 over ip|proto|host|time.</summary>
    public const string SignatureHeader = "X-Altruist-Relay-Signature";
    /// <see cref="HttpContext.Items"/> key set to true on requests restored from a verified relay (they are never relayed again).
    public const string RelayedItem = "altruist:fleet:relayed";

    private static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(60);

    /// <summary>Request headers the relay passes on (auth, cookies, client identity).</summary>
    private static readonly string[] Forwarded = { "Authorization", "Cookie", "User-Agent", "Origin", "Accept-Language" };

    private readonly RequestDelegate _next;
    private readonly IFleet _fleet;
    private readonly byte[]? _key;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the middleware. Installed automatically by the startup pipeline (before routing and shields) whenever an
    /// <see cref="IFleet"/> is registered; you do not add it yourself.
    /// </summary>
    /// <remarks>Without a secret (<c>altruist:server:fleet:secret</c> or <c>altruist:security:key</c>) nothing is relayed and incoming relay headers are stripped and ignored. Relays connect over plain <c>ws://</c> to the target's internal address.</remarks>
    /// <param name="next">Next middleware.</param>
    /// <param name="fleet">Fleet used to find the target server.</param>
    /// <param name="config">Configuration (relay secret).</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public FleetRelayMiddleware(RequestDelegate next, IFleet fleet, IConfiguration? config = null, ILoggerFactory? loggerFactory = null)
    {
        _next = next;
        _fleet = fleet;
        var secret = config?["altruist:server:fleet:secret"];
        if (string.IsNullOrEmpty(secret)) secret = config?["altruist:security:key"];
        _key = string.IsNullOrEmpty(secret) ? null : SHA256.HashData(Encoding.UTF8.GetBytes("altruist-fleet-relay:" + secret));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FleetRelayMiddleware>();
    }

    /// <summary>Restores a verified relayed client's identity, then relays a WebSocket request for another live server or passes it on.</summary>
    /// <param name="context">HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        RestoreRelayedClient(context);

        if (context.WebSockets.IsWebSocketRequest
            && context.Request.Query.TryGetValue(_fleet.NodeParam, out var wanted)
            && wanted.ToString() is { Length: > 0 } nodeId
            && nodeId != _fleet.NodeId
            && !context.Items.ContainsKey(RelayedItem) // never relay twice
            && _fleet.Find(nodeId) is { InternalAddress: { Length: > 0 } address } target
            && _key is not null)
        {
            if (await TryRelayAsync(context, target, address)) return;
        }
        await _next(context);
    }

    // ------------------------------------------------------------------ receiving side

    private void RestoreRelayedClient(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (!headers.TryGetValue(ForHeader, out var forValue)) return;
        var ip = forValue.ToString();
        var proto = headers[ProtoHeader].ToString();
        var host = headers[HostHeader].ToString();
        var ok = _key is not null
            && headers.TryGetValue(TimeHeader, out var time)
            && long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            && Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix)).TotalSeconds) <= MaxSkew.TotalSeconds
            && headers.TryGetValue(SignatureHeader, out var sig)
            && Verify(Payload(ip, proto, host, unix), sig.ToString())
            && IPAddress.TryParse(ip, out _);
        // Strip them either way: nothing behind this middleware may trust them.
        foreach (var h in new[] { ForHeader, ProtoHeader, HostHeader, TimeHeader, SignatureHeader })
            headers.Remove(h);
        if (!ok)
        {
            _logger.LogWarning("Ignored relay headers from {Peer} with a missing, stale or wrong signature.", context.Connection.RemoteIpAddress);
            return;
        }
        // The request as the relaying server received it from the client.
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (proto is "http" or "https") context.Request.Scheme = proto;
        if (host.Length > 0) context.Request.Host = new HostString(host);
        context.Items[RelayedItem] = true;
    }

    // ------------------------------------------------------------------ relaying side

    private async Task<bool> TryRelayAsync(HttpContext context, FleetNodeInfo target, string address)
    {
        var request = context.Request;
        var uri = new UriBuilder("ws", HostOf(address), PortOf(address))
        {
            Path = (request.PathBase + request.Path).Value ?? "/",
            Query = request.QueryString.HasValue ? request.QueryString.Value!.TrimStart('?') : "",
        }.Uri;

        var upstream = new ClientWebSocket();
        foreach (var name in Forwarded)
            if (request.Headers.TryGetValue(name, out var v) && v.Count > 0)
                upstream.Options.SetRequestHeader(name, v.ToString());
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);

        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "";
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var proto = request.Scheme;
        var host = request.Host.Value ?? "";
        upstream.Options.SetRequestHeader(ForHeader, clientIp);
        upstream.Options.SetRequestHeader(ProtoHeader, proto);
        upstream.Options.SetRequestHeader(HostHeader, host);
        upstream.Options.SetRequestHeader(TimeHeader, unix.ToString(CultureInfo.InvariantCulture));
        upstream.Options.SetRequestHeader(SignatureHeader, Sign(Payload(clientIp, proto, host, unix)));
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await upstream.ConnectAsync(uri, connectTimeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            // The target is gone or refused (e.g. it rejected the auth): serve here instead.
            _logger.LogInformation("Relay to {Node} at {Address} failed ({Reason}); serving the connection locally.", target.NodeId, address, ex.Message);
            upstream.Dispose();
            return false;
        }

        using (upstream)
        {
            using var client = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var down = PumpAsync(upstream, client, done.Token);
            var up = PumpAsync(client, upstream, done.Token);
            var first = await Task.WhenAny(down, up);
            // One side closed and the close went on: let the other side answer it (the close
            // handshake), then stop whatever is left.
            var other = first == down ? up : down;
            if (await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5))) != other)
                done.Cancel();
            try
            { await Task.WhenAll(down, up); }
            catch (Exception) { /* the other side closed or aborted */ }
        }
        return true;
    }

    /// <summary>Copies frames from one socket to the other until a close; the close is passed on.</summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await from.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await to.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, CancellationToken.None);
                    return;
                }
                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested || from.State != WebSocketState.Open || to.State != WebSocketState.Open)
        {
            if (to.State == WebSocketState.Open)
            {
                try
                { await to.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "relay closed", CancellationToken.None); }
                catch (Exception) { }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ------------------------------------------------------------------ signature

    private static string Payload(string ip, string proto, string host, long unix) =>
        string.Create(CultureInfo.InvariantCulture, $"{ip}|{proto}|{host}|{unix}");

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(payload)));

    private bool Verify(string payload, string signature)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(payload), expected);
        Span<byte> given = stackalloc byte[64];
        return Convert.TryFromBase64String(signature, given, out var n) && n == 32
            && CryptographicOperations.FixedTimeEquals(expected, given[..32]);
    }

    private static string HostOf(string address)
    {
        var i = address.LastIndexOf(':');
        var host = i > 0 && !address.EndsWith("]", StringComparison.Ordinal) ? address[..i] : address;
        return host.Trim('[', ']');
    }

    private static int PortOf(string address)
    {
        var i = address.LastIndexOf(':');
        return i > 0 && int.TryParse(address[(i + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 80;
    }
}
