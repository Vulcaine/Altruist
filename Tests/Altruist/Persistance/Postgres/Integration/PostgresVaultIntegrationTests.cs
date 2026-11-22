/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;

using Altruist;
using Altruist.Migrations;
using Altruist.Persistence;
using Altruist.Migrations.Postgres;
using Altruist.Persistence.Postgres;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>
/// Vault / query-translator regressions against a real Postgres: OrderBy projection, inline
/// literal escaping (also with standard_conforming_strings=off), culture-invariant numbers,
/// microsecond timestamps and the provider's fixed UTC session time zone.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresVaultIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private PgVault<ItRecord> _vault = null!;

    public PostgresVaultIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance).Migrate(new[] { typeof(ItRecord) });
        _vault = new PgVault<ItRecord>(_db.Provider, new DefaultSchema(PostgresDatabaseFixture.Schema), VaultDocument.From(typeof(ItRecord)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ItRecord> SaveAsync(string name, int rank = 0, double ratio = 0, DateTime? at = null, string? tag = null)
    {
        var record = new ItRecord
        {
            Name = name,
            Rank = rank,
            Ratio = ratio,
            HappenedAt = at ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            Tag = tag ?? Guid.NewGuid().ToString("N"),
        };
        await _vault.SaveAsync(record);
        return record;
    }

    [PostgresFact]
    public async Task OrderBy_before_Where_and_Take_still_returns_complete_rows()
    {
        var tag = "order-" + Guid.NewGuid().ToString("N")[..8];
        await SaveAsync("charlie", rank: 3, ratio: 0.3, tag: tag);
        await SaveAsync("alpha", rank: 1, ratio: 0.1, tag: tag);
        await SaveAsync("bravo", rank: 2, ratio: 0.2, tag: tag);

        var rows = await _vault.OrderBy(r => r.Rank).Where(r => r.Tag == tag).Take(2).ToListAsync();

        // Before the fix OrderBy put only the sort column into SELECT: Name/Ratio came back empty.
        Assert.Equal(new[] { "alpha", "bravo" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { 0.1, 0.2 }, rows.Select(r => r.Ratio));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.StorageId)));

        var desc = await _vault.OrderByDescending(r => r.Rank).Where(r => r.Tag == tag).FirstOrDefaultAsync();
        Assert.Equal("charlie", desc!.Name);
        Assert.Equal(3, desc.Rank);
    }

    [PostgresFact]
    public async Task Hostile_strings_in_where_clauses_match_exactly_and_never_break_the_query()
    {
        var hostile = new[] { "it's", "trailing\\", "x\\' OR '1'='1", "'; DROP TABLE altruist_it.it_records; --", "ünï\\cødé" };
        foreach (var name in hostile)
            await SaveAsync(name);

        foreach (var name in hostile)
        {
            var match = await _vault.Where(r => r.Name == name).ToListAsync();
            Assert.Single(match);
            Assert.Equal(name, match[0].Name);
        }

        Assert.Empty(await _vault.Where(r => r.Name == "x' OR '1'='1").ToListAsync());
    }

    [PostgresFact]
    public async Task Hostile_strings_are_exact_even_when_the_server_has_standard_conforming_strings_off()
    {
        var name = "ends-with-backslash\\" + Guid.NewGuid().ToString("N")[..6] + "\\";
        await SaveAsync(name);

        await _db.Exec($"ALTER DATABASE \"{_db.DatabaseName}\" SET standard_conforming_strings = off");
        NpgsqlConnection.ClearAllPools();
        try
        {
            var mode = await _db.Provider.QuerySingleAsync<ItTextRow>(
                "SELECT current_setting('standard_conforming_strings') AS \"Value\"", null, CancellationToken.None);
            Assert.Equal("off", mode!.Value);

            // A plain '...\' literal would let the backslash swallow the closing quote here.
            var match = await _vault.Where(r => r.Name == name).ToListAsync();
            Assert.Equal(name, Assert.Single(match).Name);
        }
        finally
        {
            await _db.Exec($"ALTER DATABASE \"{_db.DatabaseName}\" RESET standard_conforming_strings");
            NpgsqlConnection.ClearAllPools();
        }
    }

    [PostgresFact]
    public async Task Numeric_literals_are_culture_invariant()
    {
        var tag = "culture-" + Guid.NewGuid().ToString("N")[..8];
        await SaveAsync("ratio", ratio: 1.5, tag: tag);

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // decimal comma
        try
        {
            var ratio = 1.5;
            var rows = await _vault.Where(r => r.Tag == tag && r.Ratio == ratio).ToListAsync();
            Assert.Single(rows);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [PostgresFact]
    public async Task DateTime_literals_keep_microseconds()
    {
        var at = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified).AddTicks(1234560); // .123456
        var tag = "time-" + Guid.NewGuid().ToString("N")[..8];
        await SaveAsync("timed", at: at, tag: tag);

        // Before the fix the literal was truncated to whole seconds and matched nothing.
        var rows = await _vault.Where(r => r.Tag == tag && r.HappenedAt == at).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(at, rows[0].HappenedAt);
    }

    [PostgresFact]
    public async Task Provider_sessions_use_UTC_whatever_the_database_default_time_zone_is()
    {
        await _db.Exec($"ALTER DATABASE \"{_db.DatabaseName}\" SET timezone = 'Pacific/Auckland'");
        NpgsqlConnection.ClearAllPools();
        try
        {
            var independent = (string?)await _db.CommittedScalarAsync("SELECT current_setting('TimeZone')");
            Assert.Equal("Pacific/Auckland", independent);

            var viaProvider = await _db.Provider.QuerySingleAsync<ItTextRow>(
                "SELECT current_setting('TimeZone') AS \"Value\"", null, CancellationToken.None);
            Assert.Equal("UTC", viaProvider!.Value);
        }
        finally
        {
            await _db.Exec($"ALTER DATABASE \"{_db.DatabaseName}\" RESET timezone");
            NpgsqlConnection.ClearAllPools();
        }
    }

    [PostgresFact]
    public async Task Vault_reads_and_writes_inside_InTransactionAsync_roll_back_together()
    {
        var tag = "vault-tx-" + Guid.NewGuid().ToString("N")[..8];

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            await SaveAsync("in-tx-1", tag: tag);
            await SaveAsync("in-tx-2", tag: tag);
            // The vault sees its own uncommitted writes (same connection).
            Assert.Equal(2, (await _vault.Where(r => r.Tag == tag).ToListAsync()).Count);
            throw new InvalidOperationException("abort");
        }));

        Assert.Empty(await _vault.Where(r => r.Tag == tag).ToListAsync());
    }
}
