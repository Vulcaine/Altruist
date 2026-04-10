using Altruist;
using System.Text.Json.Serialization;

namespace AltruistProject.Portals;

/// <summary>
/// WebSocket portal — handles real-time messages from connected clients.
/// Connect via ws://localhost:8080/ and send JSON: {"event":"hello","data":{"text":"Hi!"}}
/// </summary>
[Portal("/")]
public class HelloPortal : Portal
{
    private readonly IAltruistRouter _router;

    public HelloPortal(IAltruistRouter router)
    {
        _router = router;
    }

    [Gate("hello")]
    public async Task OnHello(HelloMessage message, string clientId)
    {
        Console.WriteLine($"Client {clientId[..8]} says: {message.Text}");

        await _router.Client.SendAsync(clientId, new HelloResponse
        {
            Text = $"Hello from Altruist! You said: {message.Text}"
        });
    }

    [Gate("ping")]
    public async Task OnPing(PingMessage message, string clientId)
    {
        await _router.Client.SendAsync(clientId, new PongResponse
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }
}

// ── Messages ──

public class HelloMessage : IPacketBase
{
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; } = 1;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class HelloResponse : IPacketBase
{
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; } = 2;

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class PingMessage : IPacketBase
{
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; } = 3;
}

public class PongResponse : IPacketBase
{
    [JsonPropertyName("messageCode")]
    public uint MessageCode { get; set; } = 4;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
