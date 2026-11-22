/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Persistance.Postgres.Integration;

[Vault("it_safe_types", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItSafeTypes : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("seen")] public DateTimeOffset Seen { get; set; }
    [VaultColumn("scores")] public int[] Scores { get; set; } = Array.Empty<int>();
    [VaultColumn("tags")] public string[] Tags { get; set; } = Array.Empty<string>();
    [VaultColumn("ratios")] public double[] Ratios { get; set; } = Array.Empty<double>();
    [VaultColumn("ids")] public Guid[] Ids { get; set; } = Array.Empty<Guid>();
    [VaultColumn("amount")] public decimal Amount { get; set; }
    [VaultColumn("payload")] public Dictionary<string, int> Payload { get; set; } = new();
}

[Vault("it_safe_unmapped", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItUnmappedV1 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("legacy")] public int Legacy { get; set; }
}

/// <summary>V2 removed <c>Legacy</c> without any attribute: the column must survive.</summary>
[Vault("it_safe_unmapped", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItUnmappedV2 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

/// <summary>V3 drops it explicitly.</summary>
[Vault("it_safe_unmapped", Keyspace: PostgresDatabaseFixture.Schema)]
[VaultDropColumn("legacy")]
public sealed class ItUnmappedV3 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

[Vault("it_safe_deleted", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItToDeleteV1 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

[VaultTableDelete("gone")]
[Vault("it_safe_deleted", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItToDeleteV2 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

[Vault("it_safe_archived", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItToArchiveV1 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

[VaultArchived("it_safe_archived_old", "retired")]
[Vault("it_safe_archived", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItToArchiveV2 : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
}

[Vault("it_safe_copy", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItCopyV1 : VaultModel
{
    [VaultColumn("gold")] public int Gold { get; set; }
}

[Vault("it_safe_copy", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItCopyV2 : VaultModel
{
    [VaultColumn("gold")] public int Gold { get; set; }
    [VaultColumn("gold_text", nullable: true)][VaultColumnCopy("gold")] public string? GoldText { get; set; }
}

/// <summary>
/// Migration safety against a real Postgres: an unchanged model (timestamps, arrays, numeric, jsonb) plans nothing on
/// the next boot, unmapped columns survive with inserts still working, [VaultTableDelete] / [VaultArchived] execute,
/// and batched column copies run (Postgres has no UPDATE ... LIMIT).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresMigrationSafetyIntegrationTests
{
    private readonly PostgresDatabaseFixture _db;

    public PostgresMigrationSafetyIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    private VaultSchemaMigrator NewMigrator(int batchSize = 50_000) => new(
        new PostgresSchemaInspector(_db.Provider),
        new PostgresMigrationPlanner(),
        new PostgresMigrationExecutor(_db.Provider, batchSize),
        NullLoggerFactory.Instance);

    private async Task<IReadOnlyList<MigrationOperation>> PlanAsync(Type model)
    {
        var current = await new PostgresSchemaInspector(_db.Provider).GetCurrentModelAsync(PostgresDatabaseFixture.Schema);
        return new PostgresMigrationPlanner().Plan(
            new Dictionary<string, DatabaseModel>(StringComparer.OrdinalIgnoreCase) { [PostgresDatabaseFixture.Schema] = current },
            new[] { VaultDocument.From(model) });
    }

    private async Task<bool> TableExistsAsync(string table) =>
        (bool)(await _db.CommittedScalarAsync(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = '{PostgresDatabaseFixture.Schema}' AND table_name = '{table}')"))!;

    private async Task<bool> ColumnExistsAsync(string table, string column) =>
        (bool)(await _db.CommittedScalarAsync(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{PostgresDatabaseFixture.Schema}' AND table_name = '{table}' AND column_name = '{column}')"))!;

    private const string S = PostgresDatabaseFixture.Schema;

    [PostgresFact]
    public async Task An_unchanged_model_plans_zero_operations_on_the_next_boot()
    {
        await NewMigrator().Migrate(new[] { typeof(ItSafeTypes) });
        var ops = await PlanAsync(typeof(ItSafeTypes));
        Assert.True(ops.Count == 0, "Planned: " + string.Join(", ", ops));
    }

    [PostgresFact]
    public async Task Unmapped_columns_survive_and_inserts_still_work_until_dropped_explicitly()
    {
        await NewMigrator().Migrate(new[] { typeof(ItUnmappedV1) });
        await _db.Exec($"INSERT INTO \"{S}\".\"it_safe_unmapped\" (\"id\", \"name\", \"legacy\", \"created-at\", \"version\", \"type\") VALUES ('a', 'old', 42, now(), 1, 't')");

        await NewMigrator().Migrate(new[] { typeof(ItUnmappedV2) });
        Assert.True(await ColumnExistsAsync("it_safe_unmapped", "legacy"));
        Assert.Equal(42, Convert.ToInt32(await _db.CommittedScalarAsync($"SELECT \"legacy\" FROM \"{S}\".\"it_safe_unmapped\" WHERE id = 'a'")));

        // V2 inserts do not know "legacy" (it was NOT NULL without a default): relaxed, so this works.
        await _db.Exec($"INSERT INTO \"{S}\".\"it_safe_unmapped\" (\"id\", \"name\", \"created-at\", \"version\", \"type\") VALUES ('b', 'new', now(), 1, 't')");

        // The next boot is quiet: nothing left to relax.
        Assert.Empty(await PlanAsync(typeof(ItUnmappedV2)));

        await NewMigrator().Migrate(new[] { typeof(ItUnmappedV3) });
        Assert.False(await ColumnExistsAsync("it_safe_unmapped", "legacy"));
    }

    [PostgresFact]
    public async Task VaultTableDelete_drops_the_table()
    {
        await NewMigrator().Migrate(new[] { typeof(ItToDeleteV1) });
        Assert.True(await TableExistsAsync("it_safe_deleted"));

        await NewMigrator().Migrate(new[] { typeof(ItToDeleteV2) });
        Assert.False(await TableExistsAsync("it_safe_deleted"));
    }

    [PostgresFact]
    public async Task VaultArchived_copies_rows_then_drops_the_table()
    {
        await NewMigrator().Migrate(new[] { typeof(ItToArchiveV1) });
        await _db.Exec($"INSERT INTO \"{S}\".\"it_safe_archived\" (\"id\", \"name\", \"created-at\", \"version\", \"type\") VALUES ('a', 'keep me', now(), 1, 't')");

        await NewMigrator().Migrate(new[] { typeof(ItToArchiveV2) });
        Assert.False(await TableExistsAsync("it_safe_archived"));
        Assert.Equal("keep me", await _db.CommittedScalarAsync($"SELECT \"name\" FROM \"{S}\".\"it_safe_archived_old\" WHERE id = 'a'"));
    }

    [PostgresFact]
    public async Task Column_copies_run_in_batches()
    {
        await NewMigrator().Migrate(new[] { typeof(ItCopyV1) });
        for (var i = 0; i < 7; i++)
            await _db.Exec($"INSERT INTO \"{S}\".\"it_safe_copy\" (\"id\", \"gold\", \"created-at\", \"version\", \"type\") VALUES ('r{i}', {i * 10}, now(), 1, 't')");

        await NewMigrator(batchSize: 3).Migrate(new[] { typeof(ItCopyV2) });

        Assert.Equal(0L, Convert.ToInt64(await _db.CommittedScalarAsync(
            $"SELECT COUNT(*) FROM \"{S}\".\"it_safe_copy\" WHERE \"gold_text\" IS DISTINCT FROM \"gold\"::text")));
    }
}
