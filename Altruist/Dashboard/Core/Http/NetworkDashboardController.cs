/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Dashboard;

/// <summary>
/// In-process ring buffer of recent HTTP requests and socket packets for the dashboard's Network and
/// Performance pages. The transport and HTTP pipeline call <see cref="RecordAsync"/>; the dashboard reads
/// through <see cref="GetEvents"/> and <see cref="GetPerformance"/>.
/// </summary>
/// <remarks>
/// <para>
/// DI: singleton <see cref="IDashboardNetworkRecorder"/>, registered only when
/// <c>altruist:dashboard:enabled</c> is <c>true</c> and the <c>Altruist.Dashboard</c> assembly is loaded.
/// Settings are re-read from configuration on every call (reload-friendly):
/// <c>altruist:dashboard:network:enabled</c>, <c>:captureHttp</c>, <c>:capturePackets</c> (each on unless
/// set to <c>false</c>), <c>:retentionMinutes</c> (default 60, clamped 1..1440), <c>:maxEvents</c>
/// (default 10000, clamped 100..200000), <c>:maxPayloadBytes</c> (default 32768, clamped 0..1 MiB),
/// <c>:slowHttpMs</c> (default 1000), <c>:slowGateMs</c> (default 50) and <c>:redactFields</c>
/// (default <c>password</c>, <c>token</c>, <c>authorization</c>).
/// </para>
/// <para>
/// Memory only, per process (no sharing across a fleet). Thread-safe: one lock guards the buffer.
/// Payloads are kept as text and may contain sensitive data: JSON object fields whose names match
/// <c>redactFields</c> (case-insensitive, any depth) are replaced by <c>[redacted]</c>; non-JSON payloads
/// (including hex dumps of binary frames) are not redacted. WebSocket upgrade requests (status 101, or
/// <c>GET /ws</c>, <c>/ws/...</c>) are not recorded.
/// </para>
/// </remarks>
[Service(typeof(IDashboardNetworkRecorder), ServiceLifetime.Singleton)]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class DashboardNetworkRecorder : IDashboardNetworkRecorder
{
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _gate = new();
    private readonly Queue<DashboardNetworkEventDto> _events = new();
    private long _nextId;

    /// <summary>Creates the recorder.</summary>
    /// <param name="configuration">App configuration; the <c>altruist:dashboard:*</c> keys are read on each call.</param>
    /// <param name="jsonOptions">Options used to serialize payload objects.</param>
    public DashboardNetworkRecorder(IConfiguration configuration, JsonSerializerOptions jsonOptions)
    {
        _configuration = configuration;
        _jsonOptions = jsonOptions;
    }

    /// <summary><c>altruist:dashboard:enabled</c> is <c>true</c> and <c>altruist:dashboard:network:enabled</c> is not <c>false</c>.</summary>
    public bool IsEnabled => IsDashboardEnabled() && !IsExplicitlyFalse("altruist:dashboard:network:enabled");
    /// <summary><see cref="IsEnabled"/> and <c>altruist:dashboard:network:captureHttp</c> is not <c>false</c>.</summary>
    public bool CaptureHttp => IsEnabled && !IsExplicitlyFalse("altruist:dashboard:network:captureHttp");
    /// <summary><see cref="IsEnabled"/> and <c>altruist:dashboard:network:capturePackets</c> is not <c>false</c>.</summary>
    public bool CapturePackets => IsEnabled && !IsExplicitlyFalse("altruist:dashboard:network:capturePackets");

    /// <summary>
    /// Records one event (synchronously; the returned task is already complete). Skipped when capture of the
    /// event's kind (<c>"http"</c> / <c>"packet"</c>) is off or it is a WebSocket upgrade. The payload is taken from
    /// <paramref name="payload"/> (serialized to JSON) or else <paramref name="rawPayload"/> (UTF-8 text or hex),
    /// redacted and truncated to <c>maxPayloadBytes</c>. Old events are then pruned by age and count.
    /// </summary>
    /// <param name="entry">Event metadata (timings in milliseconds are rounded to 2 decimals).</param>
    /// <param name="payload">Optional decoded payload object.</param>
    /// <param name="rawPayload">Optional raw bytes, used only when <paramref name="payload"/> is null.</param>
    public Task RecordAsync(DashboardNetworkEvent entry, object? payload = null, byte[]? rawPayload = null)
    {
        if (!IsEnabled)
            return Task.CompletedTask;

        if (entry.Kind.Equals("http", StringComparison.OrdinalIgnoreCase) && !CaptureHttp)
            return Task.CompletedTask;

        if (entry.Kind.Equals("packet", StringComparison.OrdinalIgnoreCase) && !CapturePackets)
            return Task.CompletedTask;

        if (IsSocketUpgradeHttpEvent(entry))
            return Task.CompletedTask;

        var payloadInfo = BuildPayloadInfo(payload, rawPayload);
        var dto = new DashboardNetworkEventDto
        {
            Id = Interlocked.Increment(ref _nextId),
            TimestampUtc = DateTime.UtcNow,
            Kind = entry.Kind,
            Direction = entry.Direction,
            Transport = entry.Transport,
            Method = entry.Method,
            Path = entry.Path,
            StatusCode = entry.StatusCode,
            Route = entry.Route,
            Portal = entry.Portal,
            Gate = entry.Gate,
            Event = entry.Event,
            PacketType = entry.PacketType,
            ConnectionId = entry.ConnectionId,
            ClientId = entry.ClientId,
            RoomId = entry.RoomId,
            DurationMs = Round(entry.DurationMs),
            DecodeDurationMs = Round(entry.DecodeDurationMs),
            HandlerDurationMs = Round(entry.HandlerDurationMs),
            EncodeDurationMs = Round(entry.EncodeDurationMs),
            SendDurationMs = Round(entry.SendDurationMs),
            PayloadBytes = payloadInfo.Bytes,
            PayloadTruncated = payloadInfo.Truncated,
            PayloadPreview = payloadInfo.Preview,
            RawPayload = payloadInfo.Raw,
            Error = entry.Error
        };

        lock (_gate)
        {
            _events.Enqueue(dto);
            PruneLocked(DateTime.UtcNow);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the most recent matching events in ascending id order. Backs <c>GET /dashboard/v1/network/events</c>.
    /// </summary>
    /// <param name="sinceId">When &gt; 0, only events with a larger id (incremental polling).</param>
    /// <param name="take">Maximum events (0 or less means 300; clamped 1..2000); the newest are kept.</param>
    /// <param name="kind">Optional exact kind filter (<c>http</c> / <c>packet</c>), case-insensitive.</param>
    /// <param name="direction">Optional exact direction filter (e.g. <c>inbound</c>), case-insensitive.</param>
    /// <param name="query">Optional case-insensitive substring searched across text fields and the payload preview.</param>
    public DashboardNetworkEventsResponse GetEvents(long? sinceId, int take, string? kind, string? direction, string? query)
    {
        var safeTake = Math.Clamp(take <= 0 ? 300 : take, 1, 2000);
        List<DashboardNetworkEventDto> snapshot;
        lock (_gate)
        {
            PruneLocked(DateTime.UtcNow);
            snapshot = _events.ToList();
        }

        IEnumerable<DashboardNetworkEventDto> filtered = snapshot;

        if (sinceId is > 0)
            filtered = filtered.Where(e => e.Id > sinceId.Value);

        if (!string.IsNullOrWhiteSpace(kind))
            filtered = filtered.Where(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(direction))
            filtered = filtered.Where(e => string.Equals(e.Direction, direction, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            filtered = filtered.Where(e => Matches(e, q));
        }

        var events = filtered
            .OrderByDescending(e => e.Id)
            .Take(safeTake)
            .OrderBy(e => e.Id)
            .ToList();

        return new DashboardNetworkEventsResponse
        {
            Enabled = IsEnabled,
            RetentionMinutes = RetentionMinutes,
            MaxEvents = MaxEvents,
            MaxPayloadBytes = MaxPayloadBytes,
            Events = events
        };
    }

    /// <summary>
    /// Latency summary of the retained events: HTTP request durations by route, inbound gate handler durations,
    /// packet decode/encode and transport send timings (p50/p95/p99/max in ms, error counts), and the 10 slowest
    /// requests/gates over the <c>slowHttpMs</c> / <c>slowGateMs</c> thresholds. Backs <c>GET /dashboard/v1/performance</c>.
    /// </summary>
    public DashboardPerformanceDto GetPerformance()
    {
        List<DashboardNetworkEventDto> snapshot;
        lock (_gate)
        {
            PruneLocked(DateTime.UtcNow);
            snapshot = _events.ToList();
        }

        var http = snapshot
            .Where(e => e.Kind == "http" && e.DurationMs is not null && !IsSocketUpgradeHttpEvent(e))
            .ToList();
        var packets = snapshot.Where(e => e.Kind == "packet").ToList();

        return new DashboardPerformanceDto
        {
            Enabled = IsEnabled,
            Http = BuildTimingSummary(http, e => e.DurationMs, e => RouteKey(e)),
            Gates = BuildTimingSummary(
                packets.Where(e => e.Direction == "inbound").ToList(),
                e => e.HandlerDurationMs ?? e.DurationMs,
                e => e.Gate ?? e.Event ?? e.PacketType ?? e.Route ?? "unknown"),
            PacketDecode = BuildMetric("Packet decode", packets, e => e.DecodeDurationMs),
            PacketEncode = BuildMetric("Packet encode", packets, e => e.EncodeDurationMs),
            TransportSend = BuildMetric("Transport send", packets, e => e.SendDurationMs),
            SlowRequests = http
                .Where(e => e.DurationMs >= SlowHttpMs)
                .OrderByDescending(e => e.DurationMs)
                .Take(10)
                .ToList(),
            SlowGates = packets
                .Where(e => (e.HandlerDurationMs ?? e.DurationMs) >= SlowGateMs)
                .OrderByDescending(e => e.HandlerDurationMs ?? e.DurationMs)
                .Take(10)
                .ToList()
        };
    }

    private DashboardTimingSummaryDto BuildTimingSummary(
        IReadOnlyList<DashboardNetworkEventDto> events,
        Func<DashboardNetworkEventDto, double?> valueSelector,
        Func<DashboardNetworkEventDto, string> keySelector)
    {
        var summary = BuildMetric("All", events, valueSelector);
        summary.ByName = events
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildMetric(g.Key, g.ToList(), valueSelector))
            .Where(m => m.Count > 0)
            .OrderByDescending(m => m.P95Ms)
            .Take(20)
            .ToList();
        return summary;
    }

    private DashboardTimingSummaryDto BuildMetric(
        string name,
        IReadOnlyList<DashboardNetworkEventDto> events,
        Func<DashboardNetworkEventDto, double?> valueSelector)
    {
        var values = events
            .Select(valueSelector)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .OrderBy(v => v)
            .ToArray();

        var errors = events.Count(e => !string.IsNullOrWhiteSpace(e.Error) || e.StatusCode >= 500);

        return new DashboardTimingSummaryDto
        {
            Name = name,
            Count = values.Length,
            ErrorCount = errors,
            ErrorRate = events.Count == 0 ? 0 : Round(100d * errors / events.Count) ?? 0,
            P50Ms = Percentile(values, 0.50),
            P95Ms = Percentile(values, 0.95),
            P99Ms = Percentile(values, 0.99),
            MaxMs = values.Length == 0 ? 0 : Round(values[^1]) ?? 0
        };
    }

    private void PruneLocked(DateTime now)
    {
        var cutoff = now.AddMinutes(-RetentionMinutes);

        while (_events.Count > 0 && _events.Peek().TimestampUtc < cutoff)
            _events.Dequeue();

        while (_events.Count > MaxEvents)
            _events.Dequeue();
    }

    private PayloadInfo BuildPayloadInfo(object? payload, byte[]? rawPayload)
    {
        if (payload is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, payload.GetType(), _jsonOptions);
                json = RedactJson(json);
                return BuildTextPayload(json, Encoding.UTF8.GetByteCount(json));
            }
            catch
            {
                return BuildTextPayload(payload.ToString() ?? string.Empty, 0);
            }
        }

        if (rawPayload is null || rawPayload.Length == 0)
            return new PayloadInfo();

        var limit = MaxPayloadBytes;
        var sliceLength = Math.Min(rawPayload.Length, limit);
        var slice = rawPayload.AsSpan(0, sliceLength);
        var text = LooksLikeUtf8(slice)
            ? Encoding.UTF8.GetString(slice)
            : Convert.ToHexString(slice);

        text = RedactJson(text);
        var info = BuildTextPayload(text, rawPayload.Length);
        info.Truncated = rawPayload.Length > limit || info.Truncated;
        return info;
    }

    private PayloadInfo BuildTextPayload(string text, int byteCount)
    {
        var limit = MaxPayloadBytes;
        if (limit <= 0)
        {
            return new PayloadInfo
            {
                Bytes = byteCount,
                Truncated = !string.IsNullOrEmpty(text),
                Raw = string.Empty,
                Preview = string.Empty
            };
        }

        var truncated = Encoding.UTF8.GetByteCount(text) > limit;
        if (truncated)
            text = TruncateUtf8(text, limit);

        return new PayloadInfo
        {
            Bytes = byteCount,
            Truncated = truncated,
            Raw = text,
            Preview = text.Length > 280 ? text[..280] + "..." : text
        };
    }

    private string RedactJson(string text)
    {
        var fields = RedactFields;
        if (fields.Length == 0 || string.IsNullOrWhiteSpace(text))
            return text;

        try
        {
            var node = JsonNode.Parse(text);
            if (node is null)
                return text;

            RedactNode(node, fields);
            return node.ToJsonString(_jsonOptions);
        }
        catch
        {
            return text;
        }
    }

    private static void RedactNode(JsonNode node, string[] fields)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList())
            {
                if (fields.Any(field => string.Equals(field, kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    obj[kv.Key] = "[redacted]";
                    continue;
                }

                if (kv.Value is not null)
                    RedactNode(kv.Value, fields);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                    RedactNode(child, fields);
            }
        }
    }

    private static bool LooksLikeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = Encoding.UTF8.GetString(bytes);
            return text.Count(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t') < 2;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateUtf8(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        return Encoding.UTF8.GetString(bytes, 0, maxBytes) + "...";
    }

    private bool IsDashboardEnabled()
        => string.Equals(_configuration["altruist:dashboard:enabled"], "true", StringComparison.OrdinalIgnoreCase);

    private bool IsExplicitlyFalse(string key)
        => string.Equals(_configuration[key], "false", StringComparison.OrdinalIgnoreCase);

    private int RetentionMinutes => Math.Clamp(ReadInt("altruist:dashboard:network:retentionMinutes", 60), 1, 1440);
    private int MaxEvents => Math.Clamp(ReadInt("altruist:dashboard:network:maxEvents", 10000), 100, 200000);
    private int MaxPayloadBytes => Math.Clamp(ReadInt("altruist:dashboard:network:maxPayloadBytes", 32768), 0, 1024 * 1024);
    private double SlowHttpMs => ReadDouble("altruist:dashboard:network:slowHttpMs", 1000);
    private double SlowGateMs => ReadDouble("altruist:dashboard:network:slowGateMs", 50);
    private string[] RedactFields => _configuration
        .GetSection("altruist:dashboard:network:redactFields")
        .Get<string[]>() ?? ["password", "token", "authorization"];

    private int ReadInt(string key, int fallback)
        => int.TryParse(_configuration[key], out var value) ? value : fallback;

    private double ReadDouble(string key, double fallback)
        => double.TryParse(_configuration[key], out var value) ? value : fallback;

    private static double? Round(double? value)
        => value is null ? null : Math.Round(value.Value, 2);

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
        index = Math.Clamp(index, 0, sortedValues.Length - 1);
        return Math.Round(sortedValues[index], 2);
    }

    private static string RouteKey(DashboardNetworkEventDto e)
        => string.Join(" ", new[] { e.Method, e.Path ?? e.Route }.Where(v => !string.IsNullOrWhiteSpace(v)));

    private static bool IsSocketUpgradeHttpEvent(DashboardNetworkEvent entry)
        => IsSocketUpgradeHttpEvent(entry.Kind, entry.Method, entry.Path, entry.StatusCode);

    private static bool IsSocketUpgradeHttpEvent(DashboardNetworkEventDto entry)
        => IsSocketUpgradeHttpEvent(entry.Kind, entry.Method, entry.Path, entry.StatusCode);

    private static bool IsSocketUpgradeHttpEvent(string kind, string? method, string? path, int? statusCode)
    {
        if (!string.Equals(kind, "http", StringComparison.OrdinalIgnoreCase))
            return false;

        if (statusCode == 101)
            return true;

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(path))
            return false;

        return path.Equals("/ws", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(DashboardNetworkEventDto e, string query)
    {
        var fields = new[]
        {
            e.Kind, e.Direction, e.Transport, e.Method, e.Path, e.Route, e.Portal, e.Gate,
            e.Event, e.PacketType, e.ConnectionId, e.ClientId, e.RoomId, e.PayloadPreview, e.Error
        };

        return fields.Any(v => v?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class PayloadInfo
    {
        public int Bytes { get; set; }
        public bool Truncated { get; set; }
        public string? Preview { get; set; }
        public string? Raw { get; set; }
    }
}

/// <summary>
/// Dashboard API for recorded network traffic (route <c>/dashboard/v1/network</c>). Only mapped when
/// <c>altruist:dashboard:enabled</c> is <c>true</c>; requests must pass the dashboard protection (<see cref="DashboardAccessOptions"/>).
/// </summary>
[ApiController]
[Route("/dashboard/v1/network")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class NetworkDashboardController : ControllerBase
{
    private readonly DashboardNetworkRecorder _recorder;

    /// <summary>Creates the controller.</summary>
    /// <param name="recorder">Must be a <see cref="DashboardNetworkRecorder"/> (cast; any other implementation throws).</param>
    public NetworkDashboardController(IDashboardNetworkRecorder recorder)
    {
        _recorder = (DashboardNetworkRecorder)recorder;
    }

    /// <summary>
    /// <c>GET /dashboard/v1/network/events?sinceId=&amp;take=300&amp;kind=&amp;direction=&amp;query=</c>: 200 with a
    /// <see cref="DashboardNetworkEventsResponse"/> (see <see cref="DashboardNetworkRecorder.GetEvents"/>).
    /// </summary>
    /// <param name="sinceId">Only events with a larger id.</param>
    /// <param name="take">Maximum events returned.</param>
    /// <param name="kind">Kind filter.</param>
    /// <param name="direction">Direction filter.</param>
    /// <param name="query">Substring search.</param>
    [HttpGet("events")]
    public ActionResult<DashboardNetworkEventsResponse> GetEvents(
        [FromQuery] long? sinceId,
        [FromQuery] int take = 300,
        [FromQuery] string? kind = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? query = null)
        => Ok(_recorder.GetEvents(sinceId, take, kind, direction, query));
}

/// <summary>
/// Dashboard API for latency statistics (route <c>/dashboard/v1/performance</c>), computed from the events of
/// <see cref="DashboardNetworkRecorder"/>. Only mapped when <c>altruist:dashboard:enabled</c> is <c>true</c>; requests must pass the dashboard protection.
/// </summary>
[ApiController]
[Route("/dashboard/v1/performance")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class PerformanceDashboardController : ControllerBase
{
    private readonly DashboardNetworkRecorder _recorder;

    /// <summary>Creates the controller.</summary>
    /// <param name="recorder">Must be a <see cref="DashboardNetworkRecorder"/> (cast).</param>
    public PerformanceDashboardController(IDashboardNetworkRecorder recorder)
    {
        _recorder = (DashboardNetworkRecorder)recorder;
    }

    /// <summary><c>GET /dashboard/v1/performance</c>: 200 with a <see cref="DashboardPerformanceDto"/> (see <see cref="DashboardNetworkRecorder.GetPerformance"/>).</summary>
    [HttpGet]
    public ActionResult<DashboardPerformanceDto> GetPerformance()
        => Ok(_recorder.GetPerformance());
}

/// <summary>Response of <c>GET /dashboard/v1/network/events</c>: the events plus the recorder's current limits.</summary>
public sealed class DashboardNetworkEventsResponse
{
    /// <summary>Whether recording is currently on.</summary>
    public bool Enabled { get; set; }
    /// <summary>Effective <c>altruist:dashboard:network:retentionMinutes</c>.</summary>
    public int RetentionMinutes { get; set; }
    /// <summary>Effective <c>altruist:dashboard:network:maxEvents</c>.</summary>
    public int MaxEvents { get; set; }
    /// <summary>Effective <c>altruist:dashboard:network:maxPayloadBytes</c>.</summary>
    public int MaxPayloadBytes { get; set; }
    /// <summary>Matching events, oldest first.</summary>
    public List<DashboardNetworkEventDto> Events { get; set; } = new();
}

/// <summary>One recorded HTTP request or socket packet, as returned to the dashboard.</summary>
public sealed class DashboardNetworkEventDto
{
    /// <summary>Monotonic id within this process (use as <c>sinceId</c> for polling).</summary>
    public long Id { get; set; }
    /// <summary>When the event was recorded (UTC).</summary>
    public DateTime TimestampUtc { get; set; }
    /// <summary><c>http</c> or <c>packet</c>.</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Direction as reported by the recorder caller (e.g. <c>inbound</c>, <c>outbound</c>).</summary>
    public string Direction { get; set; } = string.Empty;
    /// <summary>Transport name as reported by the caller.</summary>
    public string Transport { get; set; } = string.Empty;
    /// <summary>HTTP method (HTTP events).</summary>
    public string? Method { get; set; }
    /// <summary>Request path (HTTP events).</summary>
    public string? Path { get; set; }
    /// <summary>HTTP status code (HTTP events).</summary>
    public int? StatusCode { get; set; }
    /// <summary>Matched route or packet route.</summary>
    public string? Route { get; set; }
    /// <summary>Portal that handled the packet, if any.</summary>
    public string? Portal { get; set; }
    /// <summary>Gate (handler) that handled the packet, if any.</summary>
    public string? Gate { get; set; }
    /// <summary>Event name, if any.</summary>
    public string? Event { get; set; }
    /// <summary>Packet type name, if any.</summary>
    public string? PacketType { get; set; }
    /// <summary>Socket connection id, if any.</summary>
    public string? ConnectionId { get; set; }
    /// <summary>Client id, if any.</summary>
    public string? ClientId { get; set; }
    /// <summary>Room id, if any.</summary>
    public string? RoomId { get; set; }
    /// <summary>Total duration in milliseconds.</summary>
    public double? DurationMs { get; set; }
    /// <summary>Packet decode time in milliseconds.</summary>
    public double? DecodeDurationMs { get; set; }
    /// <summary>Handler (gate) time in milliseconds.</summary>
    public double? HandlerDurationMs { get; set; }
    /// <summary>Packet encode time in milliseconds.</summary>
    public double? EncodeDurationMs { get; set; }
    /// <summary>Transport send time in milliseconds.</summary>
    public double? SendDurationMs { get; set; }
    /// <summary>Original payload size in bytes (0 when unknown).</summary>
    public int PayloadBytes { get; set; }
    /// <summary>Whether <see cref="RawPayload"/> was cut at <c>maxPayloadBytes</c>.</summary>
    public bool PayloadTruncated { get; set; }
    /// <summary>First 280 characters of the (redacted) payload text.</summary>
    public string? PayloadPreview { get; set; }
    /// <summary>Redacted payload text up to <c>maxPayloadBytes</c> (UTF-8 text or hex).</summary>
    public string? RawPayload { get; set; }
    /// <summary>Error message, if the request or packet failed.</summary>
    public string? Error { get; set; }
}

/// <summary>Response of <c>GET /dashboard/v1/performance</c>.</summary>
public sealed class DashboardPerformanceDto
{
    /// <summary>Whether recording is currently on.</summary>
    public bool Enabled { get; set; }
    /// <summary>HTTP request durations, grouped by method and path.</summary>
    public DashboardTimingSummaryDto Http { get; set; } = new();
    /// <summary>Inbound packet handler durations, grouped by gate/event/packet type.</summary>
    public DashboardTimingSummaryDto Gates { get; set; } = new();
    /// <summary>Packet decode timings.</summary>
    public DashboardTimingSummaryDto PacketDecode { get; set; } = new();
    /// <summary>Packet encode timings.</summary>
    public DashboardTimingSummaryDto PacketEncode { get; set; } = new();
    /// <summary>Transport send timings.</summary>
    public DashboardTimingSummaryDto TransportSend { get; set; } = new();
    /// <summary>Up to 10 slowest HTTP requests at or above <c>slowHttpMs</c>.</summary>
    public List<DashboardNetworkEventDto> SlowRequests { get; set; } = new();
    /// <summary>Up to 10 slowest packets at or above <c>slowGateMs</c>.</summary>
    public List<DashboardNetworkEventDto> SlowGates { get; set; } = new();
}

/// <summary>Latency statistics for one group of events. Times in milliseconds, rounded to 2 decimals; 0 when empty.</summary>
public sealed class DashboardTimingSummaryDto
{
    /// <summary>Group name (route, gate, or metric label).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Number of events with a timing value.</summary>
    public int Count { get; set; }
    /// <summary>Events with an error or a status code of 500 or more.</summary>
    public int ErrorCount { get; set; }
    /// <summary>Error percentage (0..100) of all events in the group.</summary>
    public double ErrorRate { get; set; }
    /// <summary>Median, ms (nearest-rank).</summary>
    public double P50Ms { get; set; }
    /// <summary>95th percentile, ms (nearest-rank).</summary>
    public double P95Ms { get; set; }
    /// <summary>99th percentile, ms (nearest-rank).</summary>
    public double P99Ms { get; set; }
    /// <summary>Maximum, ms.</summary>
    public double MaxMs { get; set; }
    /// <summary>Up to 20 sub-groups ordered by p95, slowest first (empty for leaf metrics).</summary>
    public List<DashboardTimingSummaryDto> ByName { get; set; } = new();
}
