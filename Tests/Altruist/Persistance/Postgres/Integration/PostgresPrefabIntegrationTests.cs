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

[Vault("it_prefab_players", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItPlayer : VaultModel
{
    [VaultColumn("name")] public string Name { get; set; } = "";
    [VaultColumn("guild-id", nullable: true)] public string? GuildId { get; set; }
}

[Vault("it_prefab_guilds", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItGuild : VaultModel
{
    [VaultColumn("title")] public string Title { get; set; } = "";
}

/// <summary>One per player: the key lives on this row.</summary>
[Vault("it_prefab_profiles", Keyspace: PostgresDatabaseFixture.Schema)]
[VaultUniqueKey(nameof(PlayerId))]
public sealed class ItProfile : VaultModel
{
    [VaultColumn("player-id")] public string PlayerId { get; set; } = "";
    [VaultColumn("level")] public int Level { get; set; }
}

[Vault("it_prefab_items", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItItem : VaultModel
{
    [VaultColumn("player-id"), VaultColumnIndex] public string PlayerId { get; set; } = "";
    [VaultColumn("kind")] public string Kind { get; set; } = "";
}

public sealed class ItPlayerPrefab : PrefabModel
{
    [PrefabComponentRoot] public ItPlayer Player { get; set; } = default!;

    [PrefabComponentOwned(nameof(Player), nameof(ItProfile.PlayerId))]
    public ItProfile? Profile { get; set; }

    [PrefabComponentRef(nameof(Player), nameof(ItItem.PlayerId))]
    public List<ItItem> Items { get; set; } = default!;

    [PrefabComponentRef(nameof(Player), nameof(ItPlayer.GuildId))]
    public ItGuild? Guild { get; set; }
}

/// <summary>Items are not unique per player: declaring one as owned is a modelling error the loader reports.</summary>
public sealed class ItPlayerOneItemPrefab : PrefabModel
{
    [PrefabComponentRoot] public ItPlayer Player { get; set; } = default!;

    [PrefabComponentOwned(nameof(Player), nameof(ItItem.PlayerId))]
    public ItItem? Item { get; set; }
}

public sealed class ItOwnedListPrefab : PrefabModel
{
    [PrefabComponentRoot] public ItPlayer Player { get; set; } = default!;

    [PrefabComponentOwned(nameof(Player), nameof(ItItem.PlayerId))]
    public List<ItItem> Items { get; set; } = default!;
}

public sealed class ItRefAndOwnedPrefab : PrefabModel
{
    [PrefabComponentRoot] public ItPlayer Player { get; set; } = default!;

    [PrefabComponentRef(nameof(Player), nameof(ItProfile.PlayerId))]
    [PrefabComponentOwned(nameof(Player), nameof(ItProfile.PlayerId))]
    public ItProfile? Profile { get; set; }
}

/// <summary>Owned prefab components and Contains filters, declared on the prefab (no server).</summary>
public sealed class PrefabOwnedDeclarationTests
{
    [Fact]
    public void owned_component_resolves_with_its_foreign_key_on_the_dependent()
    {
        var comp = PrefabDocument.Get<ItPlayerPrefab>().ComponentsByName[nameof(ItPlayerPrefab.Profile)];
        Assert.Equal(PrefabComponentKind.Owned, comp.Kind);
        Assert.Equal(typeof(ItProfile), comp.ComponentType);
        Assert.Equal(nameof(ItProfile.PlayerId), comp.ForeignKeyPropertyName);
    }

    [Fact]
    public void owned_list_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PrefabDocument.Get<ItOwnedListPrefab>());
        Assert.Contains("single IVaultModel", ex.Message);
    }

    [Fact]
    public void ref_and_owned_on_one_property_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PrefabDocument.Get<ItRefAndOwnedPrefab>());
        Assert.Contains("cannot be both", ex.Message);
    }
}

/// <summary>Prefab loading against a real server: owned components, Contains filters, saves.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class PostgresPrefabIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private PgPrefabs _prefabs = null!;

    public PostgresPrefabIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
            new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance)
            .Migrate(new[] { typeof(ItPlayer), typeof(ItGuild), typeof(ItProfile), typeof(ItItem) });
        foreach (var table in new[] { "it_prefab_players", "it_prefab_guilds", "it_prefab_profiles", "it_prefab_items" })
            await _db.Provider.ExecuteAsync($"DELETE FROM \"{PostgresDatabaseFixture.Schema}\".\"{table}\"", null, CancellationToken.None);
        _prefabs = new PgPrefabs(_db.Provider);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<ItPlayer> Player(string name, int? level = null, string? guildId = null, params string[] items)
    {
        var player = new ItPlayer { StorageId = NewId(), Name = name, GuildId = guildId };
        var prefab = new ItPlayerPrefab
        {
            Player = player,
            Profile = level is null ? null : new ItProfile { StorageId = NewId(), PlayerId = player.StorageId, Level = level.Value },
            Items = items.Select(k => new ItItem { StorageId = NewId(), PlayerId = player.StorageId, Kind = k }).ToList(),
        };
        await _prefabs.SaveAsync(prefab);
        return player;
    }

    [PostgresFact]
    public async Task loads_the_owned_row_of_each_root()
    {
        var a = await Player("alice", level: 7);
        var b = await Player("bob");

        var loaded = await _prefabs.Query<ItPlayerPrefab>().Include(p => p.Profile).ToListAsync();

        Assert.Equal(7, loaded.Single(p => p.Player.StorageId == a.StorageId).Profile!.Level);
        Assert.Null(loaded.Single(p => p.Player.StorageId == b.StorageId).Profile);
    }

    [PostgresFact]
    public async Task contains_loads_only_the_listed_roots_with_every_component()
    {
        var guild = new ItGuild { Title = "Wardens" };
        await new PgVault<ItGuild>(_db.Provider, new DefaultSchema(PostgresDatabaseFixture.Schema), VaultDocument.From(typeof(ItGuild))).SaveAsync(guild);
        var a = await Player("alice", level: 3, guildId: guild.StorageId, "sword", "shield");
        var b = await Player("bob", level: 5);
        await Player("carol", level: 9);
        var ids = new[] { a.StorageId, b.StorageId };

        var loaded = await _prefabs.Query<ItPlayerPrefab>().Where(p => ids.Contains(p.Player.StorageId)).IncludeAll().ToListAsync();

        Assert.Equal(new[] { "alice", "bob" }, loaded.Select(p => p.Player.Name).Order());
        var alice = loaded.Single(p => p.Player.Name == "alice");
        Assert.Equal(3, alice.Profile!.Level);
        Assert.Equal(new[] { "shield", "sword" }, alice.Items.Select(i => i.Kind).Order());
        Assert.Equal("Wardens", alice.Guild!.Title);
        Assert.Empty(loaded.Single(p => p.Player.Name == "bob").Items);
    }

    [PostgresFact]
    public async Task contains_on_a_list_and_on_nothing()
    {
        var a = await Player("alice");
        await Player("bob");
        var names = new List<string> { "alice", "nobody" };

        var byList = await _prefabs.Query<ItPlayerPrefab>().Where(p => names.Contains(p.Player.Name)).ToListAsync();
        var none = await _prefabs.Query<ItPlayerPrefab>().Where(p => Array.Empty<string>().Contains(p.Player.StorageId)).ToListAsync();

        Assert.Equal(a.StorageId, Assert.Single(byList).Player.StorageId);
        Assert.Empty(none);
    }

    [PostgresFact]
    public async Task filters_roots_on_an_owned_member()
    {
        await Player("alice", level: 3);
        await Player("bob", level: 12);
        await Player("carol");
        var levels = new[] { 12, 40 };

        var byEquals = await _prefabs.Query<ItPlayerPrefab>().Where(p => p.Profile!.Level == 3).ToListAsync();
        var byContains = await _prefabs.Query<ItPlayerPrefab>().Where(p => levels.Contains(p.Profile!.Level)).ToListAsync();

        Assert.Equal("alice", Assert.Single(byEquals).Player.Name);
        Assert.Equal("bob", Assert.Single(byContains).Player.Name);
    }

    [PostgresFact]
    public async Task a_second_owned_row_is_an_error()
    {
        await Player("alice", level: null, guildId: null, "sword", "shield");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _prefabs.Query<ItPlayerOneItemPrefab>().Include(p => p.Item).ToListAsync());
        Assert.Contains("unique key", ex.Message);
    }

    [PostgresFact]
    public async Task saving_writes_the_changed_owned_row()
    {
        var a = await Player("alice", level: 1);
        var prefab = (await _prefabs.Query<ItPlayerPrefab>().Where(p => p.Player.StorageId == a.StorageId).Include(p => p.Profile).FirstOrDefaultAsync())!;

        prefab.Profile!.Level = 2;
        await _prefabs.SaveComponentAsync(prefab, nameof(ItPlayerPrefab.Profile));

        var reloaded = await _prefabs.Query<ItPlayerPrefab>().Where(p => p.Player.StorageId == a.StorageId).Include(p => p.Profile).FirstOrDefaultAsync();
        Assert.Equal(2, reloaded!.Profile!.Level);
    }
}
