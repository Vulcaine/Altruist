/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Collections.Concurrent;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Altruist.Redis;

public sealed class RedisServiceConfiguration : ICacheConfiguration
{
    public readonly List<Type> Documents = new List<Type>();
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton<RedisConnectionSetup>();
        services.AddSingleton<ICacheConnectionSetupBase>(sp => sp.GetRequiredService<RedisConnectionSetup>());
    }

    public void AddDocument<T>() where T : IStoredModel
    {
        AddDocument(typeof(T));
    }

    public void AddDocument(Type type)
    {
        if (typeof(IStoredModel).IsAssignableFrom(type))
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
    private readonly ConcurrentDictionary<string, bool> _subscribedChannels = new();

    private readonly RedisServiceConfiguration _config;

    public RedisConnectionSetup(IServiceCollection services) : base(services)
    {
        _config = (RedisCacheServiceToken.Instance.Configuration as RedisServiceConfiguration)!;
    }

    public RedisConnectionSetup AddDocument<T>() where T : class, IStoredModel
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

        if (multiplexer.IsConnected)
        {
            logger.LogInformation("⚡ Redis support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
        }

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

        multiplexer.ConnectionFailed += (sender, args) =>
       {
           if (args.ConnectionType == ConnectionType.Interactive)
           {
               _subscribedChannels.Clear();
           }
       };

        multiplexer.ConnectionRestored += async (sender, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
            {
                await SubscribeToChannels(multiplexer, serviceProvider, logger, true, settings);
            }
        };

        await SubscribeToChannels(multiplexer, serviceProvider, logger, false, settings);
    }


    private Task SubscribeToChannels(
    IConnectionMultiplexer multiplexer,
    IServiceProvider serviceProvider,
    ILogger logger,
    bool resub,
    IAltruistContext context)
    {
        if (!multiplexer.IsConnected)
            return Task.CompletedTask;

        var subscriber = multiplexer.GetSubscriber();
        var router = serviceProvider.GetRequiredService<IAltruistRouter>();
        var decoder = serviceProvider.GetRequiredService<ICodec>();

        var channelKey = IngressRedis.MessageDistributeChannel;

        // Attempt to mark as "subscribed" atomically
        bool alreadySubscribed = !_subscribedChannels.TryAdd(channelKey, true);

        if (alreadySubscribed)
        {
            return Task.CompletedTask;
        }

        RedisChannel channel = RedisChannel.Literal(channelKey);

        logger.LogInformation(resub
            ? "🔄 Resubscribing to Redis Pub/Sub channels..."
            : "🔗 Subscribing to Redis Pub/Sub channels...");

        subscriber.Subscribe(channel, async (channel, message) =>
        {
            await ProcessQueuedMessagesAsync(multiplexer, decoder, router, context);
        });

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
