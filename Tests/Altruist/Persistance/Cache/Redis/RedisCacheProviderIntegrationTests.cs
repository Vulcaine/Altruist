/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.Redis;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

using Tests.Altruist.Framework.Scaling;

namespace Tests.Persistance.Cache.Redis;

[Vault("redis_cache_item")]
[VaultPrimaryKey("StorageId")]
public class RedisCacheItem : IStoredModel
{
    public string StorageId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

[Vault("redis_cache_special")]
[VaultPrimaryKey("StorageId")]
public class RedisSpecialCacheItem : RedisCacheItem
{
    public int Power { get; set; }
}

/// <summary>A connection factory over an existing multiplexer (the DI one connects from config).</summary>
public sealed class FixedRedisConnectionFactory(IConnectionMultiplexer mux) : RedisConnectionFactory
{
    public override IConnectionMultiplexer Multiplexer { get; } = mux;
}

/// <summary>
/// The Redis cache provider against a real Redis (env <c>ALTRUIST_TEST_REDIS</c>), in database 11 so it never touches
/// the keys of a server sharing that Redis. Regressions #32–#40, #133.
/// </summary>
public sealed class RedisCacheProviderIntegrationTests : IDisposable
{
    private const int TestDatabase = 11;

    private readonly Lazy<ConnectionMultiplexer> _mux = new(() =>
    {
        var options = ConfigurationOptions.Parse(RedisFactAttribute.Address);
        options.DefaultDatabase = TestDatabase;
        return ConnectionMultiplexer.Connect(options);
    });

    private readonly string _group = "g" + Guid.NewGuid().ToString("N");

    private RedisCacheProvider NewProvider() => new(new FixedRedisConnectionFactory(_mux.Value));

    private static RedisCacheItem Item(string id) =>
        new() { StorageId = id, Name = "n" + id, Type = VaultDocument.NameOf(typeof(RedisCacheItem)) };

    public void Dispose()
    {
        if (!_mux.IsValueCreated)
            return;
        NewProvider().ClearAllRemoteAsync().GetAwaiter().GetResult();
        _mux.Value.Dispose();
    }

    [RedisFact]
    public async Task Stored_model_types_are_discovered_without_registering_them()
    {
        var cache = NewProvider();

        await cache.SaveRemoteAsync("a", Item("a"), _group);

        Assert.Equal("na", (await cache.GetRemoteAsync<RedisCacheItem>("a", _group))!.Name);
    }

    [RedisFact]
    public async Task Batch_saved_entries_are_readable_listable_and_clearable()
    {
        var cache = NewProvider();

        await cache.SaveBatchRemoteAsync(new Dictionary<string, RedisCacheItem> { ["a"] = Item("a"), ["b"] = Item("b") }, _group);

        Assert.Equal("nb", (await cache.GetRemoteAsync<RedisCacheItem>("b", _group))!.Name);
        var listed = new List<RedisCacheItem>();
        await foreach (var item in (RedisCacheCursor<RedisCacheItem>)await cache.GetAllRemoteAsync<RedisCacheItem>(_group))
            listed.Add(item);
        Assert.Equal(new[] { "a", "b" }, listed.Select(i => i.StorageId).Order());

        await cache.ClearRemoteAsync<RedisCacheItem>(_group);
        Assert.False(await cache.ContainsAsync<RedisCacheItem>("a", _group));
        Assert.False(await cache.ContainsAsync<RedisCacheItem>("b", _group));
    }

    [RedisFact]
    public async Task The_runtime_typed_cursor_returns_the_entries()
    {
        var cache = NewProvider();
        await cache.SaveRemoteAsync("a", Item("a"), _group);

        var cursor = await cache.GetAllRemoteAsync(typeof(RedisCacheItem), _group);

        Assert.NotNull(cursor);
        var entries = new List<object>();
        foreach (var entry in cursor)
            entries.Add(entry);
        Assert.Equal("a", Assert.IsType<RedisCacheItem>(Assert.Single(entries)).StorageId);
    }

    [RedisFact]
    public async Task A_cursor_returns_every_entry_once_in_batches_with_its_stored_subtype()
    {
        var cache = NewProvider();
        for (var i = 0; i < 5; i++)
            await cache.SaveRemoteAsync("k" + i, Item("k" + i), _group);
        var special = new RedisSpecialCacheItem
        {
            StorageId = "s", Name = "special", Power = 9, Type = VaultDocument.NameOf(typeof(RedisSpecialCacheItem)),
        };
        await cache.SaveRemoteAsync<RedisCacheItem>("s", special, _group);

        var cursor = await cache.GetAllRemoteAsync<RedisCacheItem>(batchSize: 2, _group);
        var batches = new List<int>();
        var all = new List<RedisCacheItem>();
        while (cursor.HasNext)
        {
            var batch = (await cursor.NextBatch()).ToList();
            batches.Add(batch.Count);
            all.AddRange(batch);
        }

        Assert.Equal(6, all.Count);
        Assert.Equal(6, all.Select(i => i.StorageId).Distinct().Count());
        Assert.All(batches, n => Assert.True(n <= 2));
        Assert.Equal(9, Assert.IsType<RedisSpecialCacheItem>(all.Single(i => i.StorageId == "s")).Power);
    }

    [RedisFact]
    public async Task Clear_all_removes_grouped_entries_and_the_local_tier()
    {
        var cache = NewProvider();
        await cache.SaveRemoteAsync("plain", Item("plain"));
        await cache.SaveRemoteAsync("grouped", Item("grouped"), _group);

        var keys = (await cache.KeysAsync<RedisCacheItem>()).Select(k => k.ToString()).ToList();
        Assert.Contains($"redis_cache_item:plain", keys);
        Assert.Contains($"redis_cache_item_{_group}:grouped", keys);

        await cache.ClearAllRemoteAsync();

        Assert.False(await cache.ContainsAsync<RedisCacheItem>("plain"));
        Assert.False(await cache.ContainsAsync<RedisCacheItem>("grouped", _group));
        Assert.Null(await cache.GetAsync<RedisCacheItem>("grouped", _group));
    }

    [RedisFact]
    public async Task Connect_reports_the_connection_to_a_subscriber()
    {
        var cache = NewProvider();
        var connected = 0;
        cache.OnConnected += () => Interlocked.Increment(ref connected);

        await cache.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, connected);
    }

    [RedisFact]
    public void Dependency_injection_registers_one_provider_for_every_cache_interface_and_the_token()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["altruist:persistence:cache:provider"] = "redis" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<RedisConnectionFactory>(new FixedRedisConnectionFactory(_mux.Value));
        foreach (var type in new[] { typeof(RedisCacheProvider), typeof(RedisCacheServiceToken) })
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, new List<string>(), type);

        using var provider = services.BuildServiceProvider();

        var remote = provider.GetRequiredService<IRemoteCacheProvider>();
        Assert.Same(remote, provider.GetRequiredService<ICacheProvider>());
        Assert.Same(remote, provider.GetRequiredService<IRedisCacheProvider>());
        Assert.Same(RedisCacheServiceToken.Instance.Configuration,
            Assert.IsType<RedisCacheServiceToken>(provider.GetRequiredService<ICacheServiceToken>()).Configuration);
    }
}

public sealed class RedisFleetBackplaneCancellationTests : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _mux = new(() => ConnectionMultiplexer.Connect(RedisFactAttribute.Address));
    private readonly string _key = "test:cancel:" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (!_mux.IsValueCreated)
            return;
        _mux.Value.GetDatabase().KeyDelete("altruist:" + _key);
        _mux.Value.Dispose();
    }

    [RedisFact]
    public async Task A_cancelled_call_throws_and_sends_nothing()
    {
        var backplane = new RedisFleetBackplane(_mux.Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backplane.SetAsync(_key, "v", TimeSpan.FromMinutes(1), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backplane.GetByPrefixAsync(_key, cancelled.Token));

        Assert.Null(await backplane.GetAsync(_key));
    }
}
