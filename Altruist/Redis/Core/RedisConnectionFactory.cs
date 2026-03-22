/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// Factory that creates and holds the Redis connection.
/// Registered as a service; consumers inject IConnectionMultiplexer via the provider property.
/// </summary>
[Service(typeof(RedisConnectionFactory))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public class RedisConnectionFactory : IDisposable
{
    public virtual IConnectionMultiplexer Multiplexer { get; }

    protected RedisConnectionFactory() { Multiplexer = null!; }

    public RedisConnectionFactory(
        ILoggerFactory loggerFactory,
        [AppConfigValue("altruist:persistence:redis:connection-string", "localhost:6379")]
        string connectionString)
    {
        var logger = loggerFactory.CreateLogger<RedisConnectionFactory>();
        var options = ConfigurationOptions.Parse(connectionString);
        options.ReconnectRetryPolicy = new InfiniteReconnectRetryPolicy(
            loggerFactory.CreateLogger<InfiniteReconnectRetryPolicy>());
        options.AbortOnConnectFail = false;

        logger.LogInformation("Connecting to Redis: {Endpoints}", string.Join(", ", options.EndPoints));
        Multiplexer = ConnectionMultiplexer.Connect(options);
    }

    public void Dispose()
    {
        Multiplexer.Dispose();
    }
}
