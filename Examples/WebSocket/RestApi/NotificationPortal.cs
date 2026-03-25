using Altruist;
using Microsoft.Extensions.Logging;

namespace RestApi;

[Portal("/notifications")]
public class NotificationPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private readonly ILogger _logger;

    public NotificationPortal(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<NotificationPortal>();
    }

    public Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("Notification subscriber connected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        _logger.LogInformation("Notification subscriber disconnected: {ClientId}", clientId);
        return Task.CompletedTask;
    }
}
