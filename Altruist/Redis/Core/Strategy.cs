using System.Reflection;
using Altruist.Contracts;
using Altruist.UORM;
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
    RedisServiceConfiguration _config;
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

    private void BuildWithOptions(ConfigurationOptions options, ILogger logger, IAltruistContext settings)
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
            ResubscribeToChannels(multiplexer, _services.BuildServiceProvider(), logger, true, settings);
        };

        // _services.AddSingleton(sp =>
        // {
        //     var mux = sp.GetRequiredService<IConnectionMultiplexer>();
        //     //var provider = new RedisConnectionProvider(mux);

        //     if (mux.IsConnected)
        //     {
        //         BuildIndex(provider);
        //     }

        //     return provider;
        // });

        _services.AddSingleton(sp =>
        {
            var connectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
            return connectionMultiplexer.GetSubscriber();
        });

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

        _services.AddSingleton(typeof(IPlayerService<>), typeof(RedisPlayerService<>));

        var serviceProvider = _services.BuildServiceProvider();

        var mux = serviceProvider.GetRequiredService<IConnectionMultiplexer>();
        ResubscribeToChannels(mux, serviceProvider, logger, false, settings);
    }

    // private void BuildIndex(RedisConnectionProvider provider)
    // {
    //     foreach (var entity in _config.Documents)
    //     {
    //         provider.Connection.CreateIndex(entity);
    //     }
    // }

    public override void Build(IAltruistContext settings)
    {
        var sp = _services.BuildServiceProvider();
        ILoggerFactory factory = sp.GetRequiredService<ILoggerFactory>();
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
        BuildWithOptions(configOptions, logger, settings);
    }

    private void ResubscribeToChannels(IConnectionMultiplexer multiplexer, IServiceProvider serviceProvider, ILogger logger, bool resub, IAltruistContext context)
    {
        if (multiplexer.IsConnected)
        {
            var subscriber = multiplexer.GetSubscriber();
            var router = serviceProvider.GetRequiredService<IAltruistRouter>();
            var decoder = serviceProvider.GetRequiredService<ICodec>();
            var redisDatabase = multiplexer.GetDatabase();

            // reset indexes
            // var redisProvider = serviceProvider.GetRequiredService<RedisConnectionProvider>();
            // BuildIndex(redisProvider);

            if (resub)
                logger.LogInformation("🔄 Resubscribing to Redis Pub/Sub channels..");
            else
                logger.LogInformation("🔗 Subscribing to Redis Pub/Sub channels..");

            RedisChannel channel = RedisChannel.Literal(IngressRedis.MessageDistributeChannel);

            subscriber.Subscribe(channel, async (channel, message) =>
            {
                await ProcessQueuedMessagesAsync(multiplexer, decoder, router, context);
            });
        }
    }

    private async Task ProcessQueuedMessagesAsync(IConnectionMultiplexer mux, ICodec codec, IAltruistRouter router, IAltruistContext context)
    {
        var database = mux.GetDatabase();
        while (true)
        {
            var message = await database.ListRightPopAsync(IngressRedis.MessageQueue);
            if (!message.HasValue)
                break;

            var redisMessage = codec.Decoder.Decode<InterprocessPacket>(message!);

            // if we are the sender of the message, we don't process it
            if (string.IsNullOrEmpty(redisMessage.ProcessId) || redisMessage.ProcessId == context.ProcessId)
            {
                continue;
            }

            var clientId = redisMessage.Header.Receiver;
            _ = router.Client.SendAsync(clientId!, redisMessage.Message);
        }
    }


}