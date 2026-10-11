/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text;
using System.Text.Json;

using Altruist;
using Altruist.Dashboard;
using Altruist.InMemory;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.Redis;
using Altruist.UORM;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

using Tests.Altruist.Framework.Scaling;
using Tests.Altruist.Persistance.Postgres.Integration;
using Tests.Persistance.Cache.Redis;

namespace Tests.Dashboard;

/// <summary>Network recorder redaction (#46).</summary>
public sealed class DashboardNetworkRecorderRedactionTests
{
    private static DashboardNetworkRecorder Recorder(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["altruist:dashboard:enabled"] = "true" };
        foreach (var (k, v) in extra ?? new())
            values[k] = v;
        return new DashboardNetworkRecorder(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string Recorded(DashboardNetworkRecorder recorder, byte[] raw)
    {
        recorder.RecordAsync(new DashboardNetworkEvent { Kind = "http", Method = "POST", Path = "/login" }, rawPayload: raw);
        return Assert.Single(recorder.GetEvents(null, 10, null, null, null).Events).RawPayload ?? "";
    }

    [Fact]
    public void A_form_encoded_payload_is_not_recorded_because_it_cannot_be_redacted()
    {
        var text = Recorded(Recorder(), Encoding.UTF8.GetBytes("user=ann&password=hunter2"));

        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("not recorded", text);
    }

    [Fact]
    public void A_json_payload_longer_than_the_limit_is_redacted_before_it_is_truncated()
    {
        var json = "{\"user\":\"ann\",\"password\":\"hunter2\",\"padding\":\"" + new string('x', 400) + "\"}";

        var text = Recorded(Recorder(new() { ["altruist:dashboard:network:maxPayloadBytes"] = "60" }), Encoding.UTF8.GetBytes(json));

        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void A_binary_payload_is_not_recorded_unless_opted_in()
    {
        var binary = new byte[] { 0x82, 0x00, 0x01, 0x02, 0x03, 0xff };

        Assert.Contains("not recorded", Recorded(Recorder(), binary));
        Assert.Equal("82000102" + "03FF",
            Recorded(Recorder(new() { ["altruist:dashboard:network:recordNonJsonPayloads"] = "true" }), binary));
    }
}

/// <summary>Dashboard wiring that must not construct or cast (#48).</summary>
public sealed class DashboardWiringTests
{
    public sealed class LazySingleton
    {
        public static int Constructed;
        public LazySingleton() => Interlocked.Increment(ref Constructed);
    }

    [Fact]
    public void Listing_registered_services_constructs_none_of_them()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new LazySingleton());
        using var provider = services.BuildServiceProvider();
        var before = Volatile.Read(ref LazySingleton.Constructed);

        var types = provider.GetRegisteredImplementationTypes();

        Assert.Contains(typeof(LazySingleton), types);
        Assert.Equal(before, Volatile.Read(ref LazySingleton.Constructed));
    }

    [Fact]
    public void The_network_controllers_read_the_dashboard_recorder_even_when_the_interface_is_decorated()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["altruist:dashboard:enabled"] = "true" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton(new JsonSerializerOptions());
        AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, new List<string>(), typeof(DashboardNetworkRecorder));
        // An app that wraps the recorder (e.g. to also forward events elsewhere).
        services.AddSingleton<IDashboardNetworkRecorder>(new Moq.Mock<IDashboardNetworkRecorder>().Object);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(ActivatorUtilities.CreateInstance<NetworkDashboardController>(provider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<PerformanceDashboardController>(provider));
    }
}

/// <summary>Cache edits reach the remote store with a remote provider (#48).</summary>
public sealed class DashboardRemoteCacheEditTests : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _mux = new(() =>
    {
        var options = ConfigurationOptions.Parse(RedisFactAttribute.Address);
        options.DefaultDatabase = 11;
        return ConnectionMultiplexer.Connect(options);
    });

    private readonly string _group = "dash" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (!_mux.IsValueCreated)
            return;
        new RedisCacheProvider(new FixedRedisConnectionFactory(_mux.Value)).ClearRemoteAsync<RedisCacheItem>(_group).GetAwaiter().GetResult();
        _mux.Value.Dispose();
    }

    [RedisFact]
    public async Task Edits_and_deletes_are_written_through_to_redis()
    {
        var cache = new RedisCacheProvider(new FixedRedisConnectionFactory(_mux.Value));
        await cache.SaveAsync("k", new RedisCacheItem { StorageId = "k", Name = "old" }, _group);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var controller = new CacheDashboardController(cache, new InMemoryCache(), json);
        var typeName = typeof(RedisCacheItem).AssemblyQualifiedName!;

        var put = await controller.UpdateEntry(new CacheEntryUpdateDto
        {
            Type = typeName, GroupId = _group, Key = "k",
            Value = JsonSerializer.SerializeToElement(new RedisCacheItem { StorageId = "k", Name = "new" }, json),
        }, CancellationToken.None);

        Assert.IsType<NoContentResult>(put);
        Assert.Equal("new", (await cache.GetRemoteAsync<RedisCacheItem>("k", _group))!.Name);

        var delete = await controller.DeleteEntry(new CacheEntryKeyDto { Type = typeName, GroupId = _group, Key = "k" }, CancellationToken.None);

        Assert.IsType<NoContentResult>(delete);
        Assert.False(await cache.ContainsAsync<RedisCacheItem>("k", _group));
    }
}

[Vault("it_dashboard_rows", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItDashboardRow : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("rank")] public int Rank { get; set; }
}

/// <summary>Vault batch updates (#47): case-insensitive fields, all or nothing, cancellable.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class DashboardVaultBatchUpdateTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private PgVault<ItDashboardRow> _vault = null!;
    private VaultDashboardController _controller = null!;
    private string _typeKey = "";

    public DashboardVaultBatchUpdateTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance).Migrate(new[] { typeof(ItDashboardRow) });
        _vault = new PgVault<ItDashboardRow>(_db.Provider, new DefaultSchema(PostgresDatabaseFixture.Schema), VaultDocument.From(typeof(ItDashboardRow)));
        VaultRegistry.Register(typeof(ItDashboardRow), PostgresDatabaseFixture.Schema);
        _typeKey = VaultRegistry.GetTypeKey(typeof(ItDashboardRow));

        var services = new ServiceCollection();
        services.AddSingleton<IVault<ItDashboardRow>>(_vault);
        services.AddSingleton(_db.DataSource);
        _controller = new VaultDashboardController(services.BuildServiceProvider());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ItDashboardRow> Row(string name, int rank)
    {
        var row = new ItDashboardRow { Name = name, Rank = rank };
        await _vault.SaveAsync(row);
        return row;
    }

    private async Task<ItDashboardRow> Reload(ItDashboardRow row) =>
        (await _vault.Where(r => r.StorageId == row.StorageId).FirstOrDefaultAsync())!;

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    [PostgresFact]
    public async Task Rows_are_updated_with_field_names_in_any_case()
    {
        var a = await Row("a", 1);

        var result = await _controller.BatchUpdate(_typeKey, new VaultDashboardController.VaultBatchUpdateRequestDto
        {
            Items = { new() { ["storageid"] = J(a.StorageId), ["RANK"] = J(7), ["name"] = J("renamed") } },
        });

        Assert.Equal(1, Assert.IsType<VaultDashboardController.VaultBatchUpdateResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Updated);
        var reloaded = await Reload(a);
        Assert.Equal(7, reloaded.Rank);
        Assert.Equal("renamed", reloaded.Name);
    }

    [PostgresFact]
    public async Task A_failing_item_leaves_every_row_unchanged()
    {
        var a = await Row("a", 1);

        var result = await _controller.BatchUpdate(_typeKey, new VaultDashboardController.VaultBatchUpdateRequestDto
        {
            Items =
            {
                new() { ["StorageId"] = J(a.StorageId), ["Rank"] = J(2) },
                new() { ["StorageId"] = J("no-such-row"), ["Rank"] = J(3) },
            },
        });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(1, (await Reload(a)).Rank);
    }

    [PostgresFact]
    public async Task An_invalid_item_is_rejected_before_anything_is_written()
    {
        var a = await Row("a", 1);

        var result = await _controller.BatchUpdate(_typeKey, new VaultDashboardController.VaultBatchUpdateRequestDto
        {
            Items =
            {
                new() { ["StorageId"] = J(a.StorageId), ["Rank"] = J(2) },
                new() { ["StorageId"] = J(a.StorageId), ["rank"] = J(3), ["Rank"] = J(4) },
            },
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(1, (await Reload(a)).Rank);
    }

    [PostgresFact]
    public async Task A_cancelled_update_changes_nothing()
    {
        var a = await Row("a", 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _controller.BatchUpdate(_typeKey,
            new VaultDashboardController.VaultBatchUpdateRequestDto { Items = { new() { ["StorageId"] = J(a.StorageId), ["Rank"] = J(5) } } },
            cancelled.Token));

        Assert.Equal(1, (await Reload(a)).Rank);
    }
}
