/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.UORM;

using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>History-enabled model for the history window tests.</summary>
[Vault("it_history", StoreHistory: true, Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItHistoryRecord : VaultModel
{
    [VaultColumn("label")] public string Label { get; set; } = "";
}

/// <summary>
/// Query semantics of the Postgres vault against a real server: paging composition, counting, multi-key ordering,
/// projections, deletes, cursors, null comparisons and the history window.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresVaultQueryIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private PgVault<ItRecord> _vault = null!;
    private PgVault<ItHistoryRecord> _history = null!;

    public PostgresVaultQueryIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance)
            .Migrate(new[] { typeof(ItRecord), typeof(ItHistoryRecord) });
        var schema = new DefaultSchema(PostgresDatabaseFixture.Schema);
        _vault = new PgVault<ItRecord>(_db.Provider, schema, VaultDocument.From(typeof(ItRecord)));
        _history = new PgVault<ItHistoryRecord>(_db.Provider, schema, VaultDocument.From(typeof(ItHistoryRecord)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Saves rank 1..count under a fresh tag and returns the vault filtered to that tag.</summary>
    private async Task<IVault<ItRecord>> SeedAsync(int count, Func<int, string?>? tagOf = null)
    {
        var tag = "q-" + Guid.NewGuid().ToString("N")[..10];
        var rows = Enumerable.Range(1, count)
            .Select(i => new ItRecord { Name = $"n{i:D2}", Rank = i, Ratio = i / 10.0, Tag = tagOf?.Invoke(i) ?? tag })
            .ToList();
        await _vault.SaveBatchAsync(rows);
        return _vault.Where(r => r.Tag == tag);
    }

    private static int[] Ranks(IEnumerable<ItRecord> rows) => rows.Select(r => r.Rank).ToArray();

    [PostgresFact]
    public async Task Paging_composes_like_linq()
    {
        var q = (await SeedAsync(12)).OrderBy(r => r.Rank);

        Assert.Equal(new[] { 1, 2, 3 }, Ranks(await q.Take(10).Take(3).ToListAsync()));
        Assert.Equal(new[] { 6, 7, 8 }, Ranks(await q.Skip(2).Skip(3).Take(3).ToListAsync()));
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10 }, Ranks(await q.Take(10).Skip(4).ToListAsync()));
        Assert.Equal(3, (await q.Skip(2).Take(5).FirstOrDefaultAsync())!.Rank);
        Assert.Null(await q.Take(0).FirstOrDefaultAsync());
    }

    [PostgresFact]
    public async Task Count_counts_the_paged_window()
    {
        var q = (await SeedAsync(12)).OrderBy(r => r.Rank);

        Assert.Equal(12, await q.CountAsync());
        Assert.Equal(5, await q.Take(5).CountAsync());
        Assert.Equal(2, await q.Skip(10).CountAsync());
        Assert.Equal(0, await q.Skip(20).Take(5).CountAsync());
    }

    [PostgresFact]
    public async Task Several_sort_keys_apply_in_call_order()
    {
        var tag = "o-" + Guid.NewGuid().ToString("N")[..10];
        var rows = new[] { (3, "a"), (1, "b"), (1, "a"), (2, "c"), (3, "b"), (2, "a") }
            .Select(x => new ItRecord { Rank = x.Item1, Name = x.Item2, Tag = tag }).ToList();
        await _vault.SaveBatchAsync(rows);

        var sorted = await _vault.Where(r => r.Tag == tag).OrderByDescending(r => r.Rank).OrderBy(r => r.Name).ToListAsync();

        Assert.Equal(new[] { "3a", "3b", "2a", "2c", "1a", "1b" }, sorted.Select(r => $"{r.Rank}{r.Name}"));
    }

    [PostgresFact]
    public async Task SelectAsync_after_filters_and_paging_materializes_the_projection()
    {
        var q = (await SeedAsync(5)).OrderByDescending(r => r.Rank).Take(2);

        var rows = (await q.SelectAsync(r => new ItRecord { StorageId = r.StorageId, Rank = r.Rank })).ToList();

        Assert.Equal(new[] { 5, 4 }, Ranks(rows));
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.StorageId)));
        Assert.All(rows, r => Assert.Equal("", r.Name));
    }

    [PostgresFact]
    public async Task Delete_of_a_paged_chain_deletes_exactly_the_window()
    {
        var q = await SeedAsync(6);

        Assert.True(await q.OrderBy(r => r.Rank).Take(2).DeleteAsync());

        Assert.Equal(new[] { 3, 4, 5, 6 }, Ranks(await q.OrderBy(r => r.Rank).ToListAsync()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _vault.DeleteAsync());
        Assert.True(await q.Where(r => r.Rank > 4).DeleteAsync());
        Assert.Equal(new[] { 3, 4 }, Ranks(await q.OrderBy(r => r.Rank).ToListAsync()));
    }

    [PostgresFact]
    public async Task DeleteAll_empties_the_table()
    {
        var other = new PgVault<ItIndexed>(_db.Provider, new DefaultSchema(PostgresDatabaseFixture.Schema), VaultDocument.From(typeof(ItIndexed)));
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance).Migrate(new[] { typeof(ItIndexed) });
        await other.SaveBatchAsync(new[] { new ItIndexed { Owner = "a" }, new ItIndexed { Owner = "b" } });

        var deleted = await other.DeleteAllAsync();

        Assert.True(deleted >= 2);
        Assert.Equal(0, await other.CountAsync());
    }

    [PostgresFact]
    public async Task Cursor_reads_every_row_once_in_batches()
    {
        var q = await SeedAsync(SqlVault<ItRecord>.CursorBatchSize + 7);

        var cursor = await q.ToCursorAsync();
        var seen = new List<int>();
        var batches = 0;
        while (cursor.HasNext)
        {
            seen.AddRange(Ranks(await cursor.NextBatch()));
            batches++;
        }

        Assert.Equal(2, batches);
        Assert.Equal(Enumerable.Range(1, SqlVault<ItRecord>.CursorBatchSize + 7), seen.Order());
    }

    [PostgresFact]
    public async Task Not_equal_keeps_rows_whose_column_is_null()
    {
        var group = "g-" + Guid.NewGuid().ToString("N")[..10];
        await _vault.SaveBatchAsync(new[]
        {
            new ItRecord { Name = group, Rank = 1, Tag = null },
            new ItRecord { Name = group, Rank = 2, Tag = "x" },
            new ItRecord { Name = group, Rank = 3, Tag = "y" },
        });

        var notX = await _vault.Where(r => r.Name == group && r.Tag != "x").OrderBy(r => r.Rank).ToListAsync();
        var nullTag = await _vault.Where(r => r.Name == group && r.Tag == null).ToListAsync();
        var sameRank = await _vault.Where(r => r.Name == group && r.Rank == r.Rank).CountAsync();

        Assert.Equal(new[] { 1, 3 }, Ranks(notX));
        Assert.Equal(1, Assert.Single(nullTag).Rank);
        Assert.Equal(3, sameRank);
    }

    [PostgresFact]
    public async Task History_window_bounds_keep_sub_second_precision()
    {
        var row = new ItHistoryRecord { Label = "h-" + Guid.NewGuid().ToString("N")[..8] };
        await _history.SaveAsync(row, saveHistory: true);
        var stamp = await _db.Provider.QuerySingleAsync<ItTextRow>(
            $"SELECT to_char(\"timestamp\", 'YYYY-MM-DD HH24:MI:SS.US') AS \"Value\" FROM \"{PostgresDatabaseFixture.Schema}\".\"it_history_history\" WHERE \"id\" = ?",
            new List<object?> { row.StorageId }, CancellationToken.None);
        var written = DateTime.SpecifyKind(DateTime.Parse(stamp!.Value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var label = row.Label;

        // A window ending 1 microsecond before the write excludes it; truncating the bound to seconds included it.
        var before = await _history.History.Where(h => h.Label == label).ToListAsync(written.AddSeconds(-5), written.AddTicks(-10));
        var around = await _history.History.Where(h => h.Label == label).ToListAsync(written.AddTicks(-10), written.AddTicks(10));

        Assert.Empty(before);
        Assert.Single(around);
    }
}
