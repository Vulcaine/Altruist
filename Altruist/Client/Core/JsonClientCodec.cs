using System.Text;
using System.Text.Json;

namespace Altruist.Client;

/// <summary>
/// JSON codec — useful for WebSocket / browser clients and debugging. Use
/// <see cref="MessagePackClientCodec"/> for production binary transports.
/// </summary>
/// <remarks>
/// <para>Provider name <c>"json"</c>. UTF-8 <c>System.Text.Json</c> with
/// case-insensitive property matching and public fields included. On the WebSocket
/// transport it makes frames text instead of binary.</para>
/// <para>Reads the server's JSON envelopes <c>{"messageCode", "header", "message"}</c> (<see cref="IClientEnvelopeCodec"/>), so
/// <see cref="ClientPacketDispatcher"/> dispatches frames from a server using the JSON codec.</para>
/// </remarks>
[Service(typeof(IClientCodec))]
public sealed class JsonClientCodec : IClientEnvelopeCodec
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    /// <summary>Always <c>"json"</c>.</summary>
    public string Provider => "json";

    /// <inheritdoc/>
    public byte[] Serialize<T>(T value) =>
        value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Opts));

    /// <inheritdoc/>
    public T? Deserialize<T>(byte[] data) =>
        data is null || data.Length == 0 ? default : JsonSerializer.Deserialize<T>(data, Opts);

    /// <inheritdoc/>
    public T? Deserialize<T>(ReadOnlySpan<byte> data) =>
        data.IsEmpty ? default : JsonSerializer.Deserialize<T>(data, Opts);

    /// <inheritdoc/>
    public bool TryReadEnvelope(byte[] frame, out uint messageCode, out byte[] message)
    {
        messageCode = 0;
        message = Array.Empty<byte>();
        if (frame is null || frame.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement? code = null, inner = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "messageCode", StringComparison.OrdinalIgnoreCase))
                    code = property.Value;
                else if (string.Equals(property.Name, "message", StringComparison.OrdinalIgnoreCase))
                    inner = property.Value;
            }
            if (code is not { ValueKind: JsonValueKind.Number } c || !c.TryGetUInt32(out var mc) || inner is not { } m)
                return false;
            messageCode = mc;
            message = Encoding.UTF8.GetBytes(m.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
