using System.Text;
using System.Text.Json;

namespace Altruist.Client;

/// <summary>
/// JSON codec — useful for WebSocket / browser clients and debugging. Use
/// <see cref="MessagePackClientCodec"/> for production binary transports.
/// </summary>
[Service(typeof(IClientCodec))]
public sealed class JsonClientCodec : IClientCodec
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    public string Provider => "json";

    public byte[] Serialize<T>(T value) =>
        value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Opts));

    public T? Deserialize<T>(byte[] data) =>
        data is null || data.Length == 0 ? default : JsonSerializer.Deserialize<T>(data, Opts);

    public T? Deserialize<T>(ReadOnlySpan<byte> data) =>
        data.IsEmpty ? default : JsonSerializer.Deserialize<T>(data, Opts);
}
