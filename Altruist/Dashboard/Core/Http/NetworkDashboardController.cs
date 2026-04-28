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

    public DashboardNetworkRecorder(IConfiguration configuration, JsonSerializerOptions jsonOptions)
    {
        _configuration = configuration;
        _jsonOptions = jsonOptions;
    }

    public bool IsEnabled => IsDashboardEnabled() && !IsExplicitlyFalse("altruist:dashboard:network:enabled");
    public bool CaptureHttp => IsEnabled && !IsExplicitlyFalse("altruist:dashboard:network:captureHttp");
    public bool CapturePackets => IsEnabled && !IsExplicitlyFalse("altruist:dashboard:network:capturePackets");

    public Task RecordAsync(DashboardNetworkEvent entry, object? payload = null, byte[]? rawPayload = null)
    {
        if (!IsEnabled)
            return Task.CompletedTask;

        if (entry.Kind.Equals("http", StringComparison.OrdinalIgnoreCase) && !CaptureHttp)
            return Task.CompletedTask;

        if (entry.Kind.Equals("packet", StringComparison.OrdinalIgnoreCase) && !CapturePackets)
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

    public DashboardPerformanceDto GetPerformance()
    {
        List<DashboardNetworkEventDto> snapshot;
        lock (_gate)
        {
            PruneLocked(DateTime.UtcNow);
            snapshot = _events.ToList();
        }

        var http = snapshot.Where(e => e.Kind == "http" && e.DurationMs is not null).ToList();
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

[ApiController]
[Route("/dashboard/v1/network")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class NetworkDashboardController : ControllerBase
{
    private readonly DashboardNetworkRecorder _recorder;

    public NetworkDashboardController(IDashboardNetworkRecorder recorder)
    {
        _recorder = (DashboardNetworkRecorder)recorder;
    }

    [HttpGet("events")]
    public ActionResult<DashboardNetworkEventsResponse> GetEvents(
        [FromQuery] long? sinceId,
        [FromQuery] int take = 300,
        [FromQuery] string? kind = null,
        [FromQuery] string? direction = null,
        [FromQuery] string? query = null)
        => Ok(_recorder.GetEvents(sinceId, take, kind, direction, query));
}

[ApiController]
[Route("/dashboard/v1/performance")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class PerformanceDashboardController : ControllerBase
{
    private readonly DashboardNetworkRecorder _recorder;

    public PerformanceDashboardController(IDashboardNetworkRecorder recorder)
    {
        _recorder = (DashboardNetworkRecorder)recorder;
    }

    [HttpGet]
    public ActionResult<DashboardPerformanceDto> GetPerformance()
        => Ok(_recorder.GetPerformance());
}

public sealed class DashboardNetworkEventsResponse
{
    public bool Enabled { get; set; }
    public int RetentionMinutes { get; set; }
    public int MaxEvents { get; set; }
    public int MaxPayloadBytes { get; set; }
    public List<DashboardNetworkEventDto> Events { get; set; } = new();
}

public sealed class DashboardNetworkEventDto
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Transport { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Path { get; set; }
    public int? StatusCode { get; set; }
    public string? Route { get; set; }
    public string? Portal { get; set; }
    public string? Gate { get; set; }
    public string? Event { get; set; }
    public string? PacketType { get; set; }
    public string? ConnectionId { get; set; }
    public string? ClientId { get; set; }
    public string? RoomId { get; set; }
    public double? DurationMs { get; set; }
    public double? DecodeDurationMs { get; set; }
    public double? HandlerDurationMs { get; set; }
    public double? EncodeDurationMs { get; set; }
    public double? SendDurationMs { get; set; }
    public int PayloadBytes { get; set; }
    public bool PayloadTruncated { get; set; }
    public string? PayloadPreview { get; set; }
    public string? RawPayload { get; set; }
    public string? Error { get; set; }
}

public sealed class DashboardPerformanceDto
{
    public bool Enabled { get; set; }
    public DashboardTimingSummaryDto Http { get; set; } = new();
    public DashboardTimingSummaryDto Gates { get; set; } = new();
    public DashboardTimingSummaryDto PacketDecode { get; set; } = new();
    public DashboardTimingSummaryDto PacketEncode { get; set; } = new();
    public DashboardTimingSummaryDto TransportSend { get; set; } = new();
    public List<DashboardNetworkEventDto> SlowRequests { get; set; } = new();
    public List<DashboardNetworkEventDto> SlowGates { get; set; } = new();
}

public sealed class DashboardTimingSummaryDto
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public int ErrorCount { get; set; }
    public double ErrorRate { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    public List<DashboardTimingSummaryDto> ByName { get; set; } = new();
}
