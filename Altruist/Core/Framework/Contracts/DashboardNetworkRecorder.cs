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

public sealed class DashboardNetworkEvent
{
    public string Kind { get; set; } = "packet";
    public string Direction { get; set; } = "inbound";
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
    public string? Error { get; set; }
}

public interface IDashboardNetworkRecorder
{
    bool IsEnabled { get; }
    bool CaptureHttp { get; }
    bool CapturePackets { get; }
    Task RecordAsync(DashboardNetworkEvent entry, object? payload = null, byte[]? rawPayload = null);
}
