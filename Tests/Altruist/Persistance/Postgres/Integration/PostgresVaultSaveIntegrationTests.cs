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

using Npgsql;

namespace Tests.Altruist.Persistance.Postgres.Integration;

/// <summary>Natural-key model: its id derives from <see cref="Owner"/>.</summary>
[Vault("it_natural", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItNaturalKey : VaultModel, IIdGenerator
{
    [VaultColumn("owner")] public string Owner { get; set; } = "";
    [VaultColumn("value")] public int Value { get; set; }

    public string GenerateId() => $"natural:{Owner}";
}

/// <summary>Upserts on <see cref="Handle"/>; <see cref="Email"/> is a second unique key.</summary>
[Vault("it_unique_save", Keyspace: PostgresDatabaseFixture.Schema)]
[VaultUniqueKey(nameof(Handle))]
[VaultUniqueKey(nameof(Email))]
public sealed class ItUniqueSave : VaultModel
{
    [VaultColumn("handle")] public string Handle { get; set; } = "";
    [VaultColumn("email")] public string Email { get; set; } = "";
    [VaultColumn("points")] public int Points { get; set; }
}

/// <summary>Save semantics against a real server: ids, creation time, empty and unique-key batches, failure types.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresVaultSaveIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private PgVault<ItNaturalKey> _natural = null!;
    private PgVault<ItUniqueSave> _unique = null!;

    public PostgresVaultSaveIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance)
            .Migrate(new[] { typeof(ItNaturalKey), typeof(ItUniqueSave) });
        var schema = new DefaultSchema(PostgresDatabaseFixture.Schema);
        _natural = new PgVault<ItNaturalKey>(_db.Provider, schema, VaultDocument.From(typeof(ItNaturalKey)));
        _unique = new PgVault<ItUniqueSave>(_db.Provider, schema, VaultDocument.From(typeof(ItUniqueSave)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Fresh(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    [PostgresFact]
    public async Task A_loaded_IIdGenerator_row_keeps_its_id_when_its_natural_key_changes()
    {
        var owner = Fresh("o-");
        await _natural.SaveAsync(new ItNaturalKey { Owner = owner, Value = 1 });
        var loaded = (await _natural.Where(n => n.Owner == owner).FirstOrDefaultAsync())!;
        var renamed = Fresh("r-");

        loaded.Owner = renamed;
        await _natural.SaveAsync(loaded);

        Assert.Equal($"natural:{owner}", loaded.StorageId);
        Assert.Equal(0, await _natural.Where(n => n.Owner == owner).CountAsync());
        var stored = Assert.Single(await _natural.Where(n => n.Owner == renamed).ToListAsync());
        Assert.Equal($"natural:{owner}", stored.StorageId);
        Assert.Equal(2, stored.Version);
    }

    [PostgresFact]
    public async Task A_new_IIdGenerator_instance_upserts_the_row_of_its_natural_key()
    {
        var owner = Fresh("o-");
        await _natural.SaveAsync(new ItNaturalKey { Owner = owner, Value = 1 });

        var again = new ItNaturalKey { Owner = owner, Value = 2, Version = 1 };
        await _natural.SaveAsync(again);

        var stored = Assert.Single(await _natural.Where(n => n.Owner == owner).ToListAsync());
        Assert.Equal(2, stored.Value);
        Assert.Equal(2, again.Version);
    }

    [PostgresFact]
    public async Task Saves_keep_the_creation_time()
    {
        var handle = Fresh("h-");
        var row = new ItUniqueSave { Handle = handle, Email = handle + "@x" };
        await _unique.SaveAsync(row);
        var created = (await _unique.Where(u => u.Handle == handle).FirstOrDefaultAsync())!.Timestamp;

        await Task.Delay(20);
        var loaded = (await _unique.Where(u => u.Handle == handle).FirstOrDefaultAsync())!;
        loaded.Points = 5;
        await _unique.SaveAsync(loaded);
        // A fresh instance upserting onto the same unique key does not overwrite it either.
        await _unique.SaveAsync(new ItUniqueSave { Handle = handle, Email = handle + "@x", Points = 6, Version = 2 });

        var stored = (await _unique.Where(u => u.Handle == handle).FirstOrDefaultAsync())!;
        Assert.Equal(6, stored.Points);
        Assert.Equal(created, stored.Timestamp);
    }

    [PostgresFact]
    public async Task Saving_an_empty_batch_does_nothing()
    {
        var before = await _unique.CountAsync();
        await _unique.SaveBatchAsync(Array.Empty<ItUniqueSave>());
        Assert.Equal(before, await _unique.CountAsync());
    }

    [PostgresFact]
    public async Task Batch_upsert_on_a_unique_key_writes_the_stored_id_and_version_back()
    {
        var handle = Fresh("h-");
        var original = new ItUniqueSave { Handle = handle, Email = handle + "@x" };
        await _unique.SaveAsync(original);

        var other = new ItUniqueSave { Handle = Fresh("h-"), Email = Fresh("e-") };
        var sameKey = new ItUniqueSave { Handle = handle, Email = handle + "@x", Points = 9, Version = 1 };
        await _unique.SaveBatchAsync(new[] { other, sameKey });

        Assert.Equal(original.StorageId, sameKey.StorageId);
        Assert.Equal(2, sameKey.Version);
        Assert.Equal(1, other.Version);

        // The written-back version is current: saving the same instance again succeeds.
        sameKey.Points = 10;
        await _unique.SaveBatchAsync(new[] { sameKey });
        Assert.Equal(3, sameKey.Version);
    }

    [PostgresFact]
    public async Task Batch_version_mismatch_is_an_optimistic_concurrency_failure()
    {
        var handle = Fresh("h-");
        var row = new ItUniqueSave { Handle = handle, Email = handle + "@x" };
        await _unique.SaveAsync(row);

        var stale = new ItUniqueSave { StorageId = row.StorageId, Handle = handle, Email = handle + "@x", Version = 7 };
        await Assert.ThrowsAsync<OptimisticConcurrencyException>(() => _unique.SaveBatchAsync(new[] { stale }));
    }

    [PostgresFact]
    public async Task Batch_constraint_violations_are_not_reported_as_concurrency_failures()
    {
        var email = Fresh("e-") + "@x";
        await _unique.SaveAsync(new ItUniqueSave { Handle = Fresh("h-"), Email = email });

        var clash = new ItUniqueSave { Handle = Fresh("h-"), Email = email };
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _unique.SaveBatchAsync(new[] { clash }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }
}
