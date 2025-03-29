using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Altruist.Redis;

public sealed class InfiniteReconnectRetryPolicy : IReconnectRetryPolicy
{
    private readonly ILogger _logger;
    public InfiniteReconnectRetryPolicy(ILogger logger)
    {
        _logger = logger;
    }

    public bool ShouldRetry(long currentRetryCount, int timeElapsedMillisecondsSinceLastRetry)
    {
        var shouldRetry = timeElapsedMillisecondsSinceLastRetry > 5000;
        return shouldRetry;
    }
}