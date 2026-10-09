/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// Creates and holds the single Redis connection used by the Redis cache provider
/// (<see cref="RedisCacheProvider"/>). Consumers inject this factory and read <see cref="Multiplexer"/>;
/// the <see cref="IConnectionMultiplexer"/> itself is not registered in DI.
/// </summary>
/// <remarks>
/// <para>
/// DI: singleton, registered only when <c>altruist:persistence:cache:provider</c> is <c>redis</c>.
/// Connection string: <c>altruist:persistence:redis:connection-string</c> (default
/// <c>localhost:6379</c>). The fleet backplane (<see cref="RedisFleetBackplane"/>,
/// <c>altruist:server:fleet:redis</c>) opens its own separate connection.
/// </para>
/// <para>
/// Connects synchronously in the constructor with <c>AbortOnConnectFail = false</c> (startup does not
/// fail when Redis is down) and <see cref="InfiniteReconnectRetryPolicy"/> (reconnect attempts never stop).
/// </para>
/// </remarks>
[Service(typeof(RedisConnectionFactory))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public class RedisConnectionFactory : IDisposable
{
    /// <summary>The shared, thread-safe StackExchange.Redis connection. Virtual so tests can substitute a mock.</summary>
    public virtual IConnectionMultiplexer Multiplexer { get; }

    /// <summary>For mocking frameworks only: leaves <see cref="Multiplexer"/> null; override it in the subclass.</summary>
    protected RedisConnectionFactory() { Multiplexer = null!; }

    /// <summary>The constructor DI uses: parses the connection string, logs the endpoints and connects.</summary>
    /// <param name="loggerFactory">Creates the factory's and the retry policy's loggers.</param>
    /// <param name="connectionString">StackExchange.Redis connection string from <c>altruist:persistence:redis:connection-string</c>.</param>
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

    /// <summary>Closes and disposes <see cref="Multiplexer"/>.</summary>
    public void Dispose()
    {
        Multiplexer.Dispose();
    }
}
