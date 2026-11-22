/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Persistence.Postgres;
using Altruist.Querying;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>A note attached to an <see cref="ItRecord"/>.</summary>
[Vault("it_notes", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItNote : VaultModel
{
    [VaultColumn("record-id")] public string RecordId { get; set; } = "";
    [VaultColumn("text", nullable: true)] public string? Text { get; set; }
}

/// <summary>Projection target with a parameterless constructor.</summary>
public sealed class ItNoteRow
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>Projection target without a parameterless constructor.</summary>
public sealed record ItNotePair(string Name, string? Text);

/// <summary>The join layer against a real server.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresJoinIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private ServiceProvider _services = null!;
    private PgVault<ItRecord> _records = null!;
    private PgVault<ItNote> _notes = null!;

    public PostgresJoinIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance)
            .Migrate(new[] { typeof(ItRecord), typeof(ItNote) });
        var schema = new DefaultSchema(PostgresDatabaseFixture.Schema);
        _records = new PgVault<ItRecord>(_db.Provider, schema, VaultDocument.From(typeof(ItRecord)));
        _notes = new PgVault<ItNote>(_db.Provider, schema, VaultDocument.From(typeof(ItNote)));
        _services = new ServiceCollection()
            .AddSingleton<IVault<ItRecord>>(_records)
            .AddSingleton<IVault<ItNote>>(_notes)
            .BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
    }

    /// <summary>Five records (rank 1..5) under a fresh tag, each with two notes; the second note of rank 3 has no text.</summary>
    private async Task<string> SeedAsync()
    {
        var tag = "j-" + Guid.NewGuid().ToString("N")[..10];
        var records = Enumerable.Range(1, 5).Select(i => new ItRecord { Name = $"{tag}-{i}", Rank = i, Tag = tag }).ToList();
        await _records.SaveBatchAsync(records);
        var notes = records.SelectMany(r => new[]
        {
            new ItNote { RecordId = r.StorageId, Text = $"{r.Name}-a" },
            new ItNote { RecordId = r.StorageId, Text = r.Rank == 3 ? null : $"{r.Name}-b" },
        }).ToList();
        await _notes.SaveBatchAsync(notes);
        return tag;
    }

    private static IVaultQuery Query => new PgVaultQuery();

    /// <summary>Resolves the vaults of this test (Dependencies scopes are async-local, so each test pushes its own).</summary>
    private IDisposable UseVaults() => Dependencies.PushScope(_services);

    [PostgresFact]
    public async Task Root_filters_and_paging_before_Join_pick_the_joined_rows()
    {
        using var vaults = UseVaults();
        var tag = await SeedAsync();

        var rows = await Query.From<ItRecord>().Where(r => r.Tag == tag && r.Rank >= 2).OrderBy(r => r.Rank).Take(2)
            .Join<ItNote>(r => r.StorageId, n => n.RecordId)
            .ToListAsync();

        Assert.Equal(new[] { 2, 2, 3, 3 }, rows.Select(r => r.Rank));
    }

    [PostgresFact]
    public async Task Count_counts_joined_rows_and_honours_paging()
    {
        using var vaults = UseVaults();
        var tag = await SeedAsync();
        var q = Query.From<ItRecord>().Where(r => r.Tag == tag).Join<ItNote>(r => r.StorageId, n => n.RecordId);

        Assert.Equal(10, await q.CountAsync());
        Assert.Equal(4, await q.OrderBy((r, n) => n.Text).Take(4).CountAsync());
        Assert.Equal(2, await q.OrderBy((r, n) => n.Text).Skip(8).CountAsync());
    }

    [PostgresFact]
    public async Task Null_comparisons_in_join_predicates_match_null_rows()
    {
        using var vaults = UseVaults();
        var tag = await SeedAsync();

        var rows = await Query.From<ItRecord>().Where(r => r.Tag == tag)
            .Join<ItNote>(r => r.StorageId, n => n.RecordId)
            .Where((r, n) => n.Text == null)
            .ToListAsync();

        Assert.Equal(3, Assert.Single(rows).Rank);
    }

    [PostgresFact]
    public async Task A_model_can_be_joined_to_itself()
    {
        using var vaults = UseVaults();
        var tag = await SeedAsync();

        var pairs = await Query.From<ItRecord>().Where(r => r.Tag == tag)
            .Join<ItRecord>(a => a.Tag!, b => b.Tag!)
            .Where((a, b) => a.Rank < b.Rank)
            .CountAsync();

        Assert.Equal(10, pairs);
    }

    [PostgresFact]
    public async Task Projections_materialize_dtos_records_and_anonymous_types()
    {
        using var vaults = UseVaults();
        var tag = await SeedAsync();
        var q = Query.From<ItRecord>().Where(r => r.Tag == tag && r.Rank == 1)
            .Join<ItNote>(r => r.StorageId, n => n.RecordId)
            .OrderBy((r, n) => n.Text);

        var dtos = await q.SelectAsync((r, n) => new ItNoteRow { Name = r.Name, Text = n.Text ?? "" });
        var anon = await q.SelectAsync((r, n) => new { r.Name, n.Text, r.Rank });

        Assert.Equal(new[] { $"{tag}-1-a", $"{tag}-1-b" }, dtos.Select(d => d.Text));
        Assert.Equal(new[] { $"{tag}-1-a", $"{tag}-1-b" }, anon.Select(a => a.Text));
        Assert.All(anon, a => Assert.Equal(1, a.Rank));

        var records = await _db.Provider.QueryAsync<ItNotePair>(
            $"SELECT 'x' AS \"Name\", NULL AS \"Text\"", null, CancellationToken.None);
        Assert.Equal(new ItNotePair("x", null), Assert.Single(records));
    }

    [PostgresFact]
    public async Task Join_queries_observe_cancellation()
    {
        using var vaults = UseVaults();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var q = Query.From<ItRecord>().Join<ItNote>(r => r.StorageId, n => n.RecordId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.ToListAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.CountAsync(cts.Token));
    }
}
