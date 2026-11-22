/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>A model in a mixed-case schema (quoted Postgres names are case-sensitive).</summary>
[Vault("MixedRows", Keyspace: "Mixed_It")]
public sealed class ItMixedCase : VaultModel
{
    [VaultColumn("Label")] public string Label { get; set; } = "";
}

/// <summary>Connection lifecycle, naming and instance behaviour of the Postgres providers against a real server.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresProviderIntegrationTests
{
    private readonly PostgresDatabaseFixture _db;

    public PostgresProviderIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    private PgSqlDbProvider NewProvider(string? database = null, int? port = null)
    {
        var csb = new NpgsqlConnectionStringBuilder(_db.ConnectionString);
        return new PgSqlDbProvider(new JsonSerializerOptions(), csb.Host!, port ?? csb.Port, csb.Username!, csb.Password ?? "",
            database ?? csb.Database!);
    }

    [PostgresFact]
    public async Task OnConnected_is_raised_on_connect_not_after_every_query()
    {
        var provider = NewProvider();
        var raised = 0;
        provider.OnConnected += () => Interlocked.Increment(ref raised);

        await provider.ConnectAsync(3, 100);
        for (var i = 0; i < 3; i++)
        {
            await provider.ExecuteCountAsync("SELECT 1", null, CancellationToken.None);
            await provider.QueryAsync<ItTextRow>("SELECT 'x' AS \"Value\"", null, CancellationToken.None);
            await provider.ExecuteAsync("SELECT 1", null, CancellationToken.None);
        }

        Assert.Equal(1, raised);
        await provider.ShutdownAsync(null, CancellationToken.None);
    }

    [PostgresFact]
    public async Task Shutdown_is_idempotent_and_the_provider_can_reconnect()
    {
        var provider = NewProvider();
        await provider.ConnectAsync(3, 100);

        await provider.ShutdownAsync(null, CancellationToken.None);
        await provider.ShutdownAsync(null, CancellationToken.None);
        Assert.False(provider.IsConnected);

        await provider.ConnectAsync(3, 100);
        await provider.ConnectAsync(3, 100);
        Assert.True(provider.IsConnected);
        Assert.Equal(1, await provider.ExecuteCountAsync("SELECT 1", null, CancellationToken.None));
        await provider.ShutdownAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task ConnectAsync_throws_once_the_retries_are_exhausted()
    {
        // Port 1 on loopback refuses connections immediately.
        var provider = new PgSqlDbProvider(new JsonSerializerOptions(), "127.0.0.1", 1, "nobody", "", "nothing");
        Exception? exhausted = null;
        provider.OnRetryExhausted += ex => exhausted = ex;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => provider.ConnectAsync(2, 10));

        Assert.Same(exhausted, thrown);
        Assert.False(provider.IsConnected);
    }

    [PostgresFact]
    public async Task Mixed_case_schema_and_table_names_are_kept()
    {
        await _db.Provider.CreateSchemaAsync("Mixed_It", CancellationToken.None);
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance).Migrate(new[] { typeof(ItMixedCase) });
        var doc = VaultDocument.From(typeof(ItMixedCase));
        var vault = new PgVault<ItMixedCase>(_db.Provider, new DefaultSchema(doc.SchemaName), doc);

        await vault.SaveAsync(new ItMixedCase { Label = "kept" });

        Assert.Equal("kept", (await vault.Where(m => m.Label == "kept").FirstOrDefaultAsync())!.Label);
        Assert.Equal(1L, await _db.Count("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = 'Mixed_It'"));
    }

    [Fact]
    public void Usernames_and_database_names_keep_their_case()
    {
        var provider = new PgSqlDbProvider(new JsonSerializerOptions(), " Db.Host ", 5432, " App_User ", "pw", " Game_DB ");
        var csb = new NpgsqlConnectionStringBuilder(provider.GetConnectionString());
        Assert.Equal("App_User", csb.Username);
        Assert.Equal("Game_DB", csb.Database);
    }

    [PostgresFact]
    public async Task A_readonly_instance_rejects_writes()
    {
        var csb = new NpgsqlConnectionStringBuilder(_db.ConnectionString);
        var replica = new PgSqlDbInstanceProvider(new JsonSerializerOptions(), "replica", csb.Host!, csb.Port, csb.Username!,
            csb.Password ?? "", csb.Database!, role: "readonly");
        var table = $"{PostgresDatabaseFixture.Schema}.ro_{Guid.NewGuid().ToString("N")[..8]}";
        await _db.Exec($"CREATE TABLE {table} (id integer)");

        Assert.Equal(0, await replica.ExecuteCountAsync($"SELECT COUNT(*) FROM {table}", null, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            replica.ExecuteAsync($"INSERT INTO {table} (id) VALUES (1)", null, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, ex.SqlState);
    }

    [Fact]
    public void An_unknown_instance_role_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PgSqlDbInstanceProvider(new JsonSerializerOptions(), "replica",
            "localhost", 5432, "u", "p", "d", role: "read-only"));
    }

    [Fact]
    public void A_vault_on_an_unknown_DbInstance_fails_instead_of_using_the_default_database()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PgSqlDbProvider(new JsonSerializerOptions(), "localhost", 5432, "u", "p", "d"));
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => PostgresServiceFactory.ResolveProvider(sp, typeof(ItRecord), "replica"));
        Assert.IsType<PgSqlDbProvider>(PostgresServiceFactory.ResolveProvider(sp, typeof(ItRecord), ""));
    }
}
