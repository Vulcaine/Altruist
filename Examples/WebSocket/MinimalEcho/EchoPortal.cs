using Altruist;
using MessagePack;
using Microsoft.Extensions.Logging;

namespace MinimalServer;

public static class EchoCodes
{
    public const uint Echo = 1000;
    public const uint Ping = 1001;
    public const uint Pong = 1002;
}

[MessagePackObject]
public class CEchoRequest : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = EchoCodes.Echo;
    [Key(1)] public string Text { get; set; } = "";
}

[MessagePackObject]
public class SEchoResponse : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = EchoCodes.Echo;
    [Key(1)] public string Text { get; set; } = "";
    [Key(2)] public long ServerTime { get; set; }
}

[MessagePackObject]
public class CPing : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = EchoCodes.Ping;
}

[MessagePackObject]
public class SPong : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = EchoCodes.Pong;
    [Key(1)] public long Timestamp { get; set; }
}

[Portal("/")]
public class EchoPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private readonly IAltruistRouter _router;
    private readonly ILogger _logger;

    public EchoPortal(IAltruistRouter router, ILoggerFactory loggerFactory)
    {
        _router = router;
        _logger = loggerFactory.CreateLogger<EchoPortal>();
    }

    public Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("Client connected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    [Gate("echo")]
    public async Task OnEcho(CEchoRequest packet, string clientId)
    {
        await _router.Client.SendAsync(clientId, new SEchoResponse
        {
            Text = packet.Text,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    [Gate("ping")]
    public async Task OnPing(CPing packet, string clientId)
    {
        await _router.Client.SendAsync(clientId, new SPong
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }
}
