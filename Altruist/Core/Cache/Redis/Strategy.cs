using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redis.OM;
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

public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<RedisConnectionSetup>();
        services.AddSingleton<ICacheConnectionSetupBase, RedisConnectionSetup>();

        services.AddSingleton<ICacheServiceToken, RedisCacheServiceToken>();
    }
}

public sealed class RedisCacheServiceToken : ICacheServiceToken
{
    public static readonly RedisCacheServiceToken Instance = new();
    public ICacheConfiguration Configuration => new RedisServiceConfiguration();

    public string Description => "💾 Cache: Redis";
}

public sealed class RedisConnectionSetup : CacheConnectionSetup<RedisConnectionSetup>, ICacheConnectionSetupBase
{
    public RedisConnectionSetup(IServiceCollection services) : base(services)
    {
    }

    public void BuildWithOptions(ConfigurationOptions options, ILogger logger)
    {
        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:6379");
        }

        var multiplexer = ConnectionMultiplexer.Connect(options);
        _services.AddSingleton<IConnectionMultiplexer>(multiplexer);

        if (!multiplexer.IsConnected)
        {
            logger.LogError($"❌ Redis connection failed to establish. It will reconnect once the connection is coming back alive.");
        }
        else
        {
            logger.LogInformation("⚡ Redis support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
        }

        multiplexer.ConnectionFailed += (sender, args) =>
        {
            logger.LogError($"❌ Redis connection lost: {args.Exception?.Message}");
        };

        multiplexer.ConnectionRestored += (sender, args) =>
        {
            logger.LogInformation("✅ Redis connection restored. Ready to store and distribute data across realms with incredible speed! 🌌");
            ResubscribeToChannels(multiplexer, _services.BuildServiceProvider(), logger, true);
        };

        _services.AddSingleton(sp =>
        {
            var mux = sp.GetRequiredService<IConnectionMultiplexer>();
            var provider = new RedisConnectionProvider(mux);

            if (mux.IsConnected)
            {
                provider.Connection.CreateIndex(typeof(Player));
            }

            return provider;
        });

        _services.AddSingleton(sp =>
        {
            var connectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
            return connectionMultiplexer.GetSubscriber();
        });

        _services.AddSingleton<IAltruistEngineRouter, RedisEngineRouter>();
        _services.AddSingleton<IAltruistRouter, RedisDirectRouter>();
        _services.AddSingleton<RedisSocketClientSender>();
        _services.AddSingleton<RedisEngineClientSender>();
        // setup cache
        _services.AddSingleton<RedisCache>();
        _services.AddSingleton<ICache>(sp => sp.GetRequiredService<RedisCache>());
        _services.AddSingleton<IAltruistRedisProvider>(sp => sp.GetRequiredService<RedisCache>());
        // setup connection store
        _services.AddSingleton<RedisConnectionService>();
        _services.AddSingleton<IConnectionStore, RedisConnectionService>(sp => sp.GetRequiredService<RedisConnectionService>());
        _services.AddSingleton<IAltruistRedisConnectionProvider>(sp => sp.GetRequiredService<RedisConnectionService>());
        // setup dedicated player service for player connection handling
        _services.AddSingleton(typeof(IPlayerService<>), typeof(RedisPlayerService<>));

        var serviceProvider = _services.BuildServiceProvider();

        var mux = serviceProvider.GetRequiredService<IConnectionMultiplexer>();
        ResubscribeToChannels(mux, serviceProvider, logger, false);
    }


    public override void Build()
    {
        ILoggerFactory factory = _services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<RedisServiceConfiguration>();

        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:6379");
        }

        var configOptions = new ConfigurationOptions();
        configOptions.AbortOnConnectFail = false;
        configOptions.EndPoints.Add(string.Join(",", _contactPoints));
        configOptions.ReconnectRetryPolicy = new InfiniteReconnectRetryPolicy(logger);
        configOptions.ConnectTimeout = 1000;
        configOptions.SyncTimeout = 1000;
        configOptions.AsyncTimeout = 1000;
        BuildWithOptions(configOptions, logger);
    }

    private void ResubscribeToChannels(IConnectionMultiplexer multiplexer, IServiceProvider serviceProvider, ILogger logger, bool resub)
    {
        if (multiplexer.IsConnected)
        {
            var subscriber = multiplexer.GetSubscriber();
            var router = serviceProvider.GetRequiredService<IAltruistRouter>();
            var decoder = serviceProvider.GetRequiredService<IMessageDecoder>();
            var redisDatabase = multiplexer.GetDatabase();

            // reset indexes
            var redisProvider = serviceProvider.GetRequiredService<RedisConnectionProvider>();
            redisProvider.Connection.CreateIndex(typeof(Player));

            if (resub)
                logger.LogInformation("🔄 Resubscribing to Redis Pub/Sub channels..");
            else
                logger.LogInformation("🔗 Subscribing to Redis Pub/Sub channels..");

            RedisChannel channel = RedisChannel.Literal(IngressRedis.MessageDistributeChannel);

            subscriber.Subscribe(channel, async (channel, message) =>
            {
                await ProcessQueuedMessagesAsync(redisDatabase, decoder, router);
            });
        }
    }

    private async Task ProcessQueuedMessagesAsync(IDatabase redisDatabase, IMessageDecoder decoder, IAltruistRouter router)
    {
        while (true)
        {
            var message = await redisDatabase.ListRightPopAsync(IngressRedis.MessageQueue);
            if (!message.HasValue)
                break;

            var redisMessage = decoder.Decode<IPacketBase>(message!);
            var clientId = redisMessage.Header.Receiver;
            await router.Client.SendAsync(clientId!, redisMessage);
        }
    }


}