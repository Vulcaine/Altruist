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

namespace Altruist;

/// <summary>Metadata of one network event (an HTTP request or a realtime packet) for the dashboard's network inspector.</summary>
/// <remarks>Fill only the fields that apply; pass it to <see cref="IDashboardNetworkRecorder.RecordAsync"/>. All durations are milliseconds.</remarks>
public sealed class DashboardNetworkEvent
{
    /// <summary><c>"http"</c> or <c>"packet"</c> (decides which capture switch applies); other values are recorded unconditionally while enabled.</summary>
    public string Kind { get; set; } = "packet";
    /// <summary><c>"inbound"</c> or <c>"outbound"</c>.</summary>
    public string Direction { get; set; } = "inbound";
    /// <summary>Transport label, e.g. <c>"http"</c>, <c>"socket"</c> or the connection type name.</summary>
    public string Transport { get; set; } = string.Empty;
    /// <summary>HTTP method (HTTP events).</summary>
    public string? Method { get; set; }
    /// <summary>Request path (HTTP events).</summary>
    public string? Path { get; set; }
    /// <summary>HTTP response status (HTTP events).</summary>
    public int? StatusCode { get; set; }
    /// <summary>Portal route the connection is on (packet events).</summary>
    public string? Route { get; set; }
    /// <summary>Portal handling the packet.</summary>
    public string? Portal { get; set; }
    /// <summary>Gate (event name) the packet was dispatched to.</summary>
    public string? Gate { get; set; }
    /// <summary>Packet event name.</summary>
    public string? Event { get; set; }
    /// <summary>Packet CLR type name.</summary>
    public string? PacketType { get; set; }
    /// <summary>Transport connection id.</summary>
    public string? ConnectionId { get; set; }
    /// <summary>Client id.</summary>
    public string? ClientId { get; set; }
    /// <summary>Room the client was in.</summary>
    public string? RoomId { get; set; }
    /// <summary>Total handling time in ms.</summary>
    public double? DurationMs { get; set; }
    /// <summary>Decode time in ms (inbound packets).</summary>
    public double? DecodeDurationMs { get; set; }
    /// <summary>Gate handler time in ms (inbound packets).</summary>
    public double? HandlerDurationMs { get; set; }
    /// <summary>Encode time in ms (outbound packets).</summary>
    public double? EncodeDurationMs { get; set; }
    /// <summary>Send time in ms (outbound packets).</summary>
    public double? SendDurationMs { get; set; }
    /// <summary>Error message when handling failed, otherwise null.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Sink for network events shown in the Altruist dashboard. The framework records HTTP requests and inbound/outbound
/// packets itself; call it directly only to record custom traffic.
/// </summary>
/// <remarks>
/// Implemented by the dashboard module (singleton, registered only when <c>altruist:dashboard:enabled</c> is <c>true</c>
/// and the dashboard assembly is present), so inject it as optional (<c>IDashboardNetworkRecorder? recorder = null</c>)
/// and check <see cref="IsEnabled"/> (or the capture flags) before doing any work to build an event.
/// </remarks>
public interface IDashboardNetworkRecorder
{
    /// <summary>Whether network recording is on at all.</summary>
    bool IsEnabled { get; }
    /// <summary>Whether <c>"http"</c> events are recorded.</summary>
    bool CaptureHttp { get; }
    /// <summary>Whether <c>"packet"</c> events are recorded.</summary>
    bool CapturePackets { get; }
    /// <summary>Records one event; returns without recording when the matching capture is off.</summary>
    /// <param name="entry">Event metadata.</param>
    /// <param name="payload">Optional decoded payload (serialized for display; may be redacted/truncated).</param>
    /// <param name="rawPayload">Optional raw bytes, used when <paramref name="payload"/> is null.</param>
    Task RecordAsync(DashboardNetworkEvent entry, object? payload = null, byte[]? rawPayload = null);
}
