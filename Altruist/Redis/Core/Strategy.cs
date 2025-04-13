using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
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

public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    public readonly List<Type> Documents = new List<Type>();
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<RedisConnectionSetup>();
        services.AddSingleton<ICacheConnectionSetupBase, RedisConnectionSetup>();
    }

    public void AddDocument<T>() where T : IModel
    {
        AddDocument(typeof(T));
    }

    public void AddDocument(Type type)
    {
        if (typeof(IModel).IsAssignableFrom(type))
        {
            Documents.Add(type);
        }
    }

}

public sealed class RedisCacheServiceToken : ICacheServiceToken
{
    public static readonly RedisCacheServiceToken Instance = new();
    public ICacheConfiguration Configuration { get; }

    private RedisCacheServiceToken()
    {
        Configuration = new RedisServiceConfiguration();
    }

    public string Description => "💾 Cache: Redis";
}

public sealed class RedisConnectionSetup : CacheConnectionSetup<RedisConnectionSetup>, ICacheConnectionSetupBase
{
    private readonly RedisServiceConfiguration _config;

    public event Func<ConnectionFailedEventArgs, ILogger, Task>? OnConnectionFailed;
    public event Func<ConnectionFailedEventArgs, ILogger, Task>? OnConnectionRestored;

    public RedisConnectionSetup(IServiceCollection services) : base(services)
    {
        _config = (RedisCacheServiceToken.Instance.Configuration as RedisServiceConfiguration)!;
    }

    public RedisConnectionSetup AddDocument<T>() where T : class, IModel
    {
        _config.AddDocument<T>();
        return this;
    }

    public RedisConnectionSetup AddDocument(Type type)
    {
        _config.AddDocument(type);
        return this;
    }

    public override async Task Build(IAltruistContext settings)
    {
        var sp = _services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<RedisServiceConfiguration>();

        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:6379");
        }

        var configOptions = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 1000,
            SyncTimeout = 1000,
            AsyncTimeout = 1000,
            ReconnectRetryPolicy = new InfiniteReconnectRetryPolicy(logger)
        };

        configOptions.EndPoints.Add(string.Join(",", _contactPoints));

        await BuildWithOptions(configOptions, logger, settings);
    }

    private async Task BuildWithOptions(ConfigurationOptions options, ILogger logger, IAltruistContext settings)
    {
        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:6379");
        }

        var multiplexer = ConnectionMultiplexer.Connect(options);
        _services.AddSingleton<IConnectionMultiplexer>(multiplexer);

        if (!multiplexer.IsConnected)
        {
            await (OnConnectionFailed?.Invoke(null!, logger)
                ?? DefaultConnectionFailedHandler(null!, logger));
        }
        else
        {
            logger.LogInformation("⚡ Redis support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
        }

        multiplexer.ConnectionFailed += async (sender, args) =>
        {
            await (OnConnectionFailed?.Invoke(args, logger)
                ?? DefaultConnectionFailedHandler(args, logger));
        };

        multiplexer.ConnectionRestored += async (sender, args) =>
        {
            await (OnConnectionRestored?.Invoke(args, logger)
                ?? DefaultConnectionRestoredHandler(args, logger, multiplexer, settings));
        };

        _services.AddSingleton(sp => multiplexer.GetSubscriber());

        if (settings.EngineEnabled)
        {
            _services.AddSingleton<IAltruistEngineRouter, RedisEngineRouter>();
            _services.AddSingleton<RedisEngineClientSender>();
        }
        else
        {
            _services.AddSingleton<IAltruistRouter, RedisDirectRouter>();
            _services.AddSingleton<RedisSocketClientSender>();
        }

        _services.AddSingleton<RedisCacheProvider>();
        _services.AddSingleton<ICacheProvider>(sp => sp.GetRequiredService<RedisCacheProvider>());
        _services.AddSingleton<IRedisCacheProvider>(sp => sp.GetRequiredService<RedisCacheProvider>());

        _services.AddSingleton<RedisConnectionService>();
        _services.AddSingleton<IConnectionStore, RedisConnectionService>(sp => sp.GetRequiredService<RedisConnectionService>());
        _services.AddSingleton<IAltruistRedisConnectionProvider>(sp => sp.GetRequiredService<RedisConnectionService>());

        var serviceProvider = _services.BuildServiceProvider();
        await ResubscribeToChannels(multiplexer, serviceProvider, logger, false, settings);
    }

    private static Task DefaultConnectionFailedHandler(ConnectionFailedEventArgs? args, ILogger logger)
    {
        logger.LogError($"❌ Redis connection failed: {args?.Exception?.Message ?? "Unknown error"}");
        return Task.CompletedTask;
    }

    private static async Task DefaultConnectionRestoredHandler(ConnectionFailedEventArgs? args, ILogger logger, IConnectionMultiplexer multiplexer, IAltruistContext context)
    {
        logger.LogInformation("✅ Redis connection restored. Re-subscribing to Redis Pub/Sub channels...");
        await new RedisConnectionSetup(null!).ResubscribeToChannels(multiplexer, null!, logger, true, context);
    }

    private Task ResubscribeToChannels(IConnectionMultiplexer multiplexer, IServiceProvider serviceProvider, ILogger logger, bool resub, IAltruistContext context)
    {
        if (multiplexer.IsConnected)
        {
            var subscriber = multiplexer.GetSubscriber();
            var router = serviceProvider.GetRequiredService<IAltruistRouter>();
            var decoder = serviceProvider.GetRequiredService<ICodec>();

            var redisDatabase = multiplexer.GetDatabase();

            logger.LogInformation(resub ? "🔄 Resubscribing to Redis Pub/Sub channels..." : "🔗 Subscribing to Redis Pub/Sub channels...");

            RedisChannel channel = RedisChannel.Literal(IngressRedis.MessageDistributeChannel);

            subscriber.Subscribe(channel, async (channel, message) =>
            {
                await ProcessQueuedMessagesAsync(multiplexer, decoder, router, context);
            });
        }

        return Task.CompletedTask;
    }

    private async Task ProcessQueuedMessagesAsync(IConnectionMultiplexer mux, ICodec codec, IAltruistRouter router, IAltruistContext context)
    {
        var database = mux.GetDatabase();
        while (true)
        {
            var message = await database.ListRightPopAsync(IngressRedis.MessageQueue);
            if (!message.HasValue) break;

            var redisMessage = codec.Decoder.Decode<InterprocessPacket>(message!);

            // Skip self-sent messages
            if (string.IsNullOrEmpty(redisMessage.ProcessId) || redisMessage.ProcessId == context.ProcessId)
                continue;

            var clientId = redisMessage.Header.Receiver;
            _ = router.Client.SendAsync(clientId!, redisMessage.Message);
        }
    }
}
