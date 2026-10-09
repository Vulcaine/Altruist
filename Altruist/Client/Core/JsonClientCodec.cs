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
/// <para>Outbound only in practice: <see cref="ClientPacketDispatcher"/> parses inbound
/// envelopes as MessagePack, so JSON-encoded server frames are not dispatched.</para>
/// </remarks>
[Service(typeof(IClientCodec))]
public sealed class JsonClientCodec : IClientCodec
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
}
