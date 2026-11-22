/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Data;
using System.Reflection;

using Altruist.Persistence;
using Altruist.Persistence.Postgres;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>
/// Regression tests for the ambient SQL transaction (SqlAmbientTransaction /
/// GeneralSqlDatabaseProvider.InTransactionAsync) and the [Transactional] DispatchProxy decorator.
/// Before the fix every provider call took its own pooled connection, so a "transaction" (and
/// the decorator's transaction) never covered the statements issued inside it.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresTransactionIntegrationTests
{
    private readonly PostgresDatabaseFixture _db;

    public PostgresTransactionIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    private async Task<string> NewTableAsync()
    {
        var table = $"{PostgresDatabaseFixture.Schema}.tx_{Guid.NewGuid().ToString("N")[..10]}";
        await _db.Exec($"CREATE TABLE {table} (id text PRIMARY KEY, v integer NOT NULL)");
        return table;
    }

    private Task<long> InsertAsync(string table, string id, int v = 1) =>
        _db.Exec($"INSERT INTO {table} (id, v) VALUES ('{id}', {v})");

    private async Task<long> CommittedCountAsync(string table) =>
        Convert.ToInt64(await _db.CommittedScalarAsync($"SELECT COUNT(*) FROM {table}"));

    private Task<long> BackendPidAsync() => _db.Count("SELECT pg_backend_pid()");

    [PostgresFact]
    public async Task Statements_inside_InTransactionAsync_share_one_connection_and_commit_together()
    {
        var table = await NewTableAsync();

        var pids = new List<long>();
        await _db.Provider.InTransactionAsync(async _ =>
        {
            pids.Add(await BackendPidAsync());
            await InsertAsync(table, "a");
            pids.Add(await BackendPidAsync());
            await InsertAsync(table, "b");
            pids.Add(await BackendPidAsync());

            // Inside the transaction: visible to the transaction itself, not to anyone else yet.
            Assert.Equal(2, await _db.Count($"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(0, await CommittedCountAsync(table));
            return true;
        });

        Assert.Single(pids.Distinct());
        Assert.Equal(2, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task InTransactionAsync_rolls_back_every_statement_when_the_work_throws()
    {
        var table = await NewTableAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            await InsertAsync(table, "a");
            await InsertAsync(table, "b");
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Nested_InTransactionAsync_joins_the_outer_transaction()
    {
        var table = await NewTableAsync();
        long outerPid = 0, innerPid = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            outerPid = await BackendPidAsync();
            await InsertAsync(table, "outer");
            await _db.Provider.InTransactionAsync(async _ =>
            {
                innerPid = await BackendPidAsync();
                await InsertAsync(table, "inner");
                return true;
            });
            // The inner block "completed", but it only joined: the outer failure undoes it too.
            throw new InvalidOperationException("outer fails after inner returned");
        }));

        Assert.Equal(outerPid, innerPid);
        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Nested_failure_swallowed_by_the_outer_block_is_committed_with_it_no_savepoints()
    {
        // Documents the implemented semantics: a nested call is a plain join (no SAVEPOINT), so
        // work done by an inner block that threw a non-SQL exception, which the outer block caught,
        // is still committed by the outer transaction.
        var table = await NewTableAsync();

        await _db.Provider.InTransactionAsync(async _ =>
        {
            try
            {
                await _db.Provider.InTransactionAsync<bool>(async _ =>
                {
                    await InsertAsync(table, "inner");
                    throw new InvalidOperationException("inner");
                });
            }
            catch (InvalidOperationException) { }

            await InsertAsync(table, "outer");
            return true;
        });

        Assert.Equal(2, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Failed_work_is_not_retried_and_the_scope_is_released_afterwards()
    {
        // Altruist itself does not retry: a failing block runs exactly once and its exception
        // (here a real PostgresException) reaches the caller. Retrying is left to the application.
        var table = await NewTableAsync();
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            attempts++;
            await InsertAsync(table, "a");
            await _db.Count("SELECT 1 / 0");
            return true;
        }));

        Assert.Equal("22012", ex.SqlState); // division_by_zero
        Assert.Equal(1, attempts);
        Assert.Null(SqlAmbientTransaction.Current);
        Assert.Equal(0, await CommittedCountAsync(table));

        // The provider is fully usable afterwards (pooled connections, auto-commit).
        await InsertAsync(table, "after");
        Assert.Equal(1, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Ambient_scope_is_cleared_after_commit()
    {
        var table = await NewTableAsync();
        await _db.Provider.InTransactionAsync(async _ =>
        {
            Assert.NotNull(SqlAmbientTransaction.Current);
            await InsertAsync(table, "a");
            return true;
        });

        Assert.Null(SqlAmbientTransaction.Current);
        // Auto-commit again: an insert is visible immediately.
        await InsertAsync(table, "b");
        Assert.Equal(2, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Parallel_awaits_inside_a_transaction_are_serialized_on_the_shared_connection()
    {
        var table = await NewTableAsync();

        await _db.Provider.InTransactionAsync(async _ =>
        {
            // Without the per-scope gate, concurrent commands on one NpgsqlConnection throw
            // "A command is already in progress".
            await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => InsertAsync(table, $"r{i}", i))));
            return true;
        });

        Assert.Equal(25, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Isolation_level_is_applied_to_the_transaction()
    {
        var level = "";
        await _db.Provider.InTransactionAsync(async _ =>
        {
            var row = await _db.Provider.QuerySingleAsync<ItTextRow>("SELECT current_setting('transaction_isolation') AS \"Value\"", null, CancellationToken.None);
            level = row!.Value;
            return true;
        }, IsolationLevel.Serializable);

        Assert.Equal("serializable", level);
    }

    // ------------------------------------------------------------------ [Transactional] decorator

    public interface ITxService
    {
        Task InsertTwoThenFailAsync(string table);
        Task<int> InsertTwoAsync(string table);
        void InsertSync(string table, string id, bool fail);
        ValueTask InsertAcrossAnAwaitAsync(string table, bool fail);
        ValueTask<int> InsertAcrossAnAwaitWithResultAsync(string table);
        IAsyncEnumerable<int> InsertWhileStreamingAsync(string table, int count, int failAt);
        Task ThrowBeforeAwaitingAsync();
    }

    private sealed class TxService : ITxService
    {
        private readonly PostgresDatabaseFixture _db;
        public TxService(PostgresDatabaseFixture db) => _db = db;

        [Transactional]
        public async Task InsertTwoThenFailAsync(string table)
        {
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('x1', 1)");
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('x2', 2)");
            throw new InvalidOperationException("fail after two inserts");
        }

        [Transactional]
        public async Task<int> InsertTwoAsync(string table)
        {
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('y1', 1)");
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('y2', 2)");
            return 2;
        }

        [Transactional]
        public void InsertSync(string table, string id, bool fail)
        {
            _db.Exec($"INSERT INTO {table} (id, v) VALUES ('{id}', 1)").GetAwaiter().GetResult();
            if (fail)
                throw new InvalidOperationException("sync fail");
        }

        [Transactional]
        public async ValueTask InsertAcrossAnAwaitAsync(string table, bool fail)
        {
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('v1', 1)");
            await Task.Delay(20);
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('v2', 2)");
            if (fail)
                throw new InvalidOperationException("fail after the await");
        }

        [Transactional]
        public async ValueTask<int> InsertAcrossAnAwaitWithResultAsync(string table)
        {
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('w1', 1)");
            await Task.Delay(20);
            await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('w2', 2)");
            return 2;
        }

        [Transactional]
        public async IAsyncEnumerable<int> InsertWhileStreamingAsync(string table, int count, int failAt)
        {
            for (var i = 1; i <= count; i++)
            {
                if (i == failAt)
                    throw new InvalidOperationException("fail while streaming");
                await _db.Exec($"INSERT INTO {table} (id, v) VALUES ('s{i}', {i})");
                yield return i;
            }
        }

        [Transactional]
        public Task ThrowBeforeAwaitingAsync() => throw new InvalidOperationException("thrown before any task exists");
    }

    private ITxService CreateDecorated()
    {
        // Same registration the Postgres configuration performs at startup: the implementation's
        // [Transactional] methods. The proxy itself is invoked with the interface's MethodInfo.
        foreach (var m in typeof(TxService).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.GetCustomAttribute<TransactionalAttribute>(inherit: true) is { } attr)
                TransactionalRegistry.Register(m, attr, typeof(TxService));

        var proxy = DispatchProxy.Create<ITxService, TransactionalDecorator<ITxService>>();
        var decorator = (TransactionalDecorator<ITxService>)(object)proxy;
        decorator.Inner = new TxService(_db);
        decorator.DataSource = _db.DataSource;
        return proxy;
    }

    [PostgresFact]
    public async Task Transactional_method_that_throws_rolls_back_the_provider_calls_it_made()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InsertTwoThenFailAsync(table));

        // Before the fix the inserts ran on their own pooled connections and stayed committed.
        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_method_commits_on_success()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        Assert.Equal(2, await service.InsertTwoAsync(table));
        Assert.Equal(2, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_sync_method_rolls_back_on_exception_and_commits_on_success()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        // The method's own exception, not a TargetInvocationException from the reflection call.
        Assert.Throws<InvalidOperationException>(() => service.InsertSync(table, "s1", fail: true));
        Assert.Equal(0, await CommittedCountAsync(table));

        service.InsertSync(table, "s2", fail: false);
        Assert.Equal(1, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_method_called_inside_an_outer_transaction_joins_it()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            await service.InsertTwoAsync(table);  // would commit on its own if it opened a second transaction
            throw new InvalidOperationException("outer fails");
        }));

        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_ValueTask_method_commits_only_after_its_work_completes()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        // Before the fix a ValueTask counted as synchronous: the transaction committed at the first await.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.InsertAcrossAnAwaitAsync(table, fail: true));
        Assert.Equal(0, await CommittedCountAsync(table));

        await service.InsertAcrossAnAwaitAsync(table, fail: false);
        Assert.Equal(2, await CommittedCountAsync(table));
        Assert.Equal(2, await service.InsertAcrossAnAwaitWithResultAsync(table));
        Assert.Equal(4, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_async_stream_commits_when_the_enumeration_completes()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        var seen = new List<int>();
        await foreach (var i in service.InsertWhileStreamingAsync(table, count: 3, failAt: 0))
        {
            seen.Add(i);
            Assert.Equal(0, await CommittedCountAsync(table));
        }

        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.Equal(3, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_async_stream_rolls_back_when_it_throws_or_is_abandoned()
    {
        var table = await NewTableAsync();
        var service = CreateDecorated();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.InsertWhileStreamingAsync(table, count: 3, failAt: 3)) { }
        });
        Assert.Equal(0, await CommittedCountAsync(table));

        await foreach (var i in service.InsertWhileStreamingAsync(table, count: 3, failAt: 0))
        {
            if (i == 2)
                break;
        }
        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [PostgresFact]
    public async Task Transactional_async_method_throwing_synchronously_surfaces_its_own_exception()
    {
        var service = CreateDecorated();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ThrowBeforeAwaitingAsync());
    }

    /// <summary>Exposes the configuration helpers the Postgres startup uses.</summary>
    private sealed class ConfigurationProbe : PostgresConfigurationBase
    {
        public static void Wrap(IServiceCollection services)
            => RegisterTransactionalServices(services, new[] { typeof(TxService).Assembly });

        public static void AddDataSource(IServiceCollection services)
            => RegisterNpgsqlDataSource(services);
    }

    [PostgresFact]
    public async Task Services_registered_through_factories_and_forwards_are_wrapped()
    {
        var table = await NewTableAsync();
        var services = new ServiceCollection();
        // The shape [Service(typeof(ITxService))] registers: the class through a factory, the interface as a forward to it.
        services.AddSingleton(_ => new TxService(_db));
        services.AddSingleton<ITxService>(sp => sp.GetRequiredService<TxService>());
        services.AddSingleton(_db.DataSource);

        ConfigurationProbe.Wrap(services);
        using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<ITxService>();

        Assert.IsAssignableFrom<TransactionalDecorator<ITxService>>(service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InsertTwoThenFailAsync(table));
        Assert.Equal(0, await CommittedCountAsync(table));
    }

    [Fact]
    public void Data_source_uses_the_default_providers_connection_settings()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PgSqlDbProvider(new System.Text.Json.JsonSerializerOptions(),
            "db.example", 6543, "app", "pw", "game", pooling: true, maxPoolSize: 17, sslMode: "require"));

        ConfigurationProbe.AddDataSource(services);
        using var sp = services.BuildServiceProvider();
        var csb = new NpgsqlConnectionStringBuilder(sp.GetRequiredService<NpgsqlDataSource>().ConnectionString);

        Assert.Equal("db.example", csb.Host);
        Assert.Equal(6543, csb.Port);
        Assert.Equal("UTC", csb.Timezone);
        Assert.Equal(SslMode.Require, csb.SslMode);
        Assert.Equal(17, csb.MaxPoolSize);
    }
}
