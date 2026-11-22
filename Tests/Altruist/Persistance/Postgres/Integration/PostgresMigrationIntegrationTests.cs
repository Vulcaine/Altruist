/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Migrations;
using Altruist.Migrations.Postgres;

using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>
/// Migration regressions against a real Postgres: atomic DDL (BEGIN/COMMIT used to go to different
/// pooled connections), a duplicate UNIQUE constraint when a unique column is added to an existing
/// table, and CREATE INDEX with a schema-qualified index name (a syntax error in Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresMigrationIntegrationTests
{
    private readonly PostgresDatabaseFixture _db;

    public PostgresMigrationIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    private VaultSchemaMigrator NewMigrator() => new(
        new PostgresSchemaInspector(_db.Provider),
        new PostgresMigrationPlanner(),
        new PostgresMigrationExecutor(_db.Provider),
        NullLoggerFactory.Instance);

    private async Task<bool> TableExistsAsync(string table) =>
        (bool)(await _db.CommittedScalarAsync(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = '{PostgresDatabaseFixture.Schema}' AND table_name = '{table}')"))!;

    [PostgresFact]
    public async Task A_failing_operation_rolls_back_the_whole_migration()
    {
        var table = "mig_atomic_" + Guid.NewGuid().ToString("N")[..8];
        var executor = new PostgresMigrationExecutor(_db.Provider);
        var ops = new MigrationOperation[]
        {
            new CreateTableOperation(PostgresDatabaseFixture.Schema, table,
                new[] { new ColumnDefinition("id", "text", false, false) }, new[] { "id" }),
            // Fails: the table does not exist.
            new AddColumnOperation(PostgresDatabaseFixture.Schema, "no_such_table_" + Guid.NewGuid().ToString("N")[..8],
                new ColumnDefinition("x", "integer", true, false)),
        };

        var ex = await Assert.ThrowsAsync<MigrationException>(() => executor.ApplyAsync(PostgresDatabaseFixture.Schema, ops));
        Assert.Contains("operation 2/2", ex.Message);
        Assert.Contains("rolled back", ex.Message);

        // Before the fix CREATE TABLE ran on another pooled connection than BEGIN/ROLLBACK: it stayed.
        Assert.False(await TableExistsAsync(table));
    }

    [PostgresFact]
    public async Task A_successful_migration_commits_every_operation()
    {
        var table = "mig_ok_" + Guid.NewGuid().ToString("N")[..8];
        var executor = new PostgresMigrationExecutor(_db.Provider);
        await executor.ApplyAsync(PostgresDatabaseFixture.Schema, new MigrationOperation[]
        {
            new CreateTableOperation(PostgresDatabaseFixture.Schema, table,
                new[] { new ColumnDefinition("id", "text", false, false) }, new[] { "id" }),
            new AddColumnOperation(PostgresDatabaseFixture.Schema, table, new ColumnDefinition("x", "integer", true, false)),
        });

        Assert.True(await TableExistsAsync(table));
        var cols = Convert.ToInt64(await _db.CommittedScalarAsync(
            $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = '{PostgresDatabaseFixture.Schema}' AND table_name = '{table}'"));
        Assert.Equal(2, cols);
    }

    [PostgresFact]
    public async Task Adding_a_unique_column_to_an_existing_table_creates_exactly_one_constraint()
    {
        var migrator = NewMigrator();
        await migrator.Migrate(new[] { typeof(ItUniqueProbeV1) });
        await migrator.Migrate(new[] { typeof(ItUniqueProbeV2) });
        await migrator.Migrate(new[] { typeof(ItUniqueProbeV2) }); // a later boot changes nothing

        var constraints = Convert.ToInt64(await _db.CommittedScalarAsync(
            "SELECT COUNT(*) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
            "JOIN pg_namespace n ON n.oid = t.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(c.conkey) " +
            $"WHERE n.nspname = '{PostgresDatabaseFixture.Schema}' AND t.relname = 'it_unique_probe' AND c.contype = 'u' AND a.attname = 'email'"));
        Assert.Equal(1, constraints);
    }

    [PostgresFact]
    public async Task Indexed_columns_get_their_index_in_the_table_schema()
    {
        await NewMigrator().Migrate(new[] { typeof(ItIndexed) });

        var indexes = Convert.ToInt64(await _db.CommittedScalarAsync(
            $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = '{PostgresDatabaseFixture.Schema}' AND tablename = 'it_indexed' AND indexdef LIKE '%(owner)%'"));
        Assert.Equal(1, indexes);
    }
}
