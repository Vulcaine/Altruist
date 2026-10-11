/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Migrations;
using Altruist.Migrations.Postgres;
using Altruist.Persistence;
using Altruist.Security;
using Altruist.UORM;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Altruist.Persistance.Postgres.Integration;

[Vault("it_refresh_tokens", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItRefreshToken : RefreshTokenModel { }

/// <summary>An existing table with its own column names, mapped by overriding the properties.</summary>
[Vault("it_legacy_sessions", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItLegacySession : RefreshTokenModel
{
    [VaultColumn("account_id"), VaultColumnIndex] public override string PrincipalId { get; set; } = "";
    [VaultColumn("secret_hash")] public override string TokenHash { get; set; } = "";
}

[Vault("it_email_links", Keyspace: PostgresDatabaseFixture.Schema)]
public sealed class ItEmailLink : OneTimeTokenModel
{
    [VaultColumn("user_id"), VaultColumnIndex] public override string SubjectId { get; set; } = "";
    [VaultColumn("email")] public override string? Payload { get; set; } = "";
}

[Collection(PostgresCollection.Name)]
[Trait("Category", PostgresTestEnvironment.Category)]
public sealed class TokenStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _db;
    private static readonly SemaphoreSlim Migrated = new(1, 1);
    private static bool _migrated;

    public TokenStoreIntegrationTests(PostgresDatabaseFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        if (!_db.Available)
            return;
        await Migrated.WaitAsync();
        try
        {
            if (!_migrated)
                await new VaultSchemaMigrator(new PostgresSchemaInspector(_db.Provider), new PostgresMigrationPlanner(),
                    new PostgresMigrationExecutor(_db.Provider), NullLoggerFactory.Instance)
                    .Migrate(new[] { typeof(ItRefreshToken), typeof(ItLegacySession), typeof(ItEmailLink) });
            _migrated = true;
        }
        finally
        {
            Migrated.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private RefreshTokenService Refresh(Type? model = null, Func<DateTime>? clock = null, int maxSessions = 3) =>
        new(_db.Provider, model ?? typeof(ItRefreshToken), new RefreshTokenOptions { MaxSessions = maxSessions }, NullLoggerFactory.Instance, clock);

    private static string Principal() => "p" + Guid.NewGuid().ToString("N")[..12];

    private Task<long> Count(string table, string where) =>
        _db.Count($"SELECT COUNT(*) FROM {PostgresDatabaseFixture.Schema}.{table} WHERE {where}");

    [PostgresFact]
    public async Task Rotation_issues_a_successor_and_stores_only_hashes()
    {
        var refresh = Refresh();
        var p = Principal();
        var first = await refresh.IssueAsync(p);
        Assert.Equal(43, first.Token.Length);
        Assert.InRange((first.ExpiresAt - DateTime.UtcNow).TotalDays, 29.9, 30.1);

        var rotated = await refresh.RotateAsync(first.Token);
        Assert.Equal(RefreshOutcome.Rotated, rotated.Outcome);
        Assert.Equal(p, rotated.PrincipalId);
        Assert.Equal(first.FamilyId, rotated.Next!.FamilyId);
        Assert.NotEqual(first.Token, rotated.Next.Token);

        Assert.Equal(2, await Count("it_refresh_tokens", $"principal_id = '{p}'"));
        Assert.Equal(1, await Count("it_refresh_tokens", $"token_hash = '{OpaqueToken.Hash(rotated.Next.Token)}'"));
        Assert.Equal(0, await Count("it_refresh_tokens", $"token_hash = '{rotated.Next.Token}'"));
    }

    [PostgresFact]
    public async Task Replaying_a_rotated_token_revokes_its_family_only()
    {
        var refresh = Refresh();
        var p = Principal();
        var first = await refresh.IssueAsync(p);
        var second = (await refresh.RotateAsync(first.Token)).Next!;
        var other = await refresh.IssueAsync(p);

        var replay = await refresh.RotateAsync(first.Token);
        Assert.Equal(RefreshOutcome.Reused, replay.Outcome);
        Assert.Equal(p, replay.PrincipalId);
        Assert.Null(replay.Next);
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(second.Token)).Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await refresh.RotateAsync(other.Token)).Outcome);

        foreach (var junk in new[] { null, "", "x' OR '1'='1", OpaqueToken.New() })
            Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(junk)).Outcome);
    }

    [PostgresFact]
    public async Task Concurrent_rotations_of_one_token_succeed_once()
    {
        var refresh = Refresh();
        var token = await refresh.IssueAsync(Principal());
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => refresh.RotateAsync(token.Token))));
        Assert.Single(results, r => r.Outcome == RefreshOutcome.Rotated);
    }

    [PostgresFact]
    public async Task Expired_tokens_do_not_rotate()
    {
        var now = DateTime.UtcNow;
        var refresh = Refresh(clock: () => now);
        var token = await refresh.IssueAsync(Principal());
        now = now.AddDays(31);
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(token.Token)).Outcome);
    }

    [PostgresFact]
    public async Task Sign_out_revokes_the_family_and_sign_out_everywhere_every_family()
    {
        var refresh = Refresh();
        var p = Principal();
        var a = await refresh.IssueAsync(p);
        var b = await refresh.IssueAsync(p);
        Assert.True(await refresh.RevokeFamilyAsync(a.Token));
        Assert.False(await refresh.RevokeFamilyAsync("garbage"));
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(a.Token)).Outcome);

        var c = await refresh.IssueAsync(p);
        Assert.Equal(2, await refresh.RevokeAllAsync(p));
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(b.Token)).Outcome);
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(c.Token)).Outcome);
    }

    [PostgresFact]
    public async Task Excess_sessions_beyond_the_cap_are_revoked_oldest_first()
    {
        var now = DateTime.UtcNow;
        var refresh = Refresh(clock: () => now, maxSessions: 3);
        var p = Principal();
        var sessions = new List<IssuedRefreshToken>();
        for (var i = 0; i < 5; i++)
        {
            now = now.AddSeconds(1);
            sessions.Add(await refresh.IssueAsync(p));
        }
        Assert.Equal(2, await refresh.RevokeExcessAsync(p));
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(sessions[0].Token)).Outcome);
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(sessions[1].Token)).Outcome);
        Assert.Equal(RefreshOutcome.Rotated, (await refresh.RotateAsync(sessions[2].Token)).Outcome);
        Assert.Equal(0, await refresh.RevokeExcessAsync(p, maxSessions: 0));
    }

    [PostgresFact]
    public async Task Prune_keeps_recently_rotated_tokens_for_replay_detection()
    {
        var refresh = Refresh();
        var p = Principal();
        var first = await refresh.IssueAsync(p);
        var next = (await refresh.RotateAsync(first.Token)).Next!;

        await refresh.PruneAsync(DateTime.UtcNow);
        Assert.Equal(2, await Count("it_refresh_tokens", $"principal_id = '{p}'"));
        Assert.Equal(RefreshOutcome.Reused, (await refresh.RotateAsync(first.Token)).Outcome);

        await refresh.PruneAsync(DateTime.UtcNow + TimeSpan.FromHours(49));
        Assert.Equal(0, await Count("it_refresh_tokens", $"principal_id = '{p}'"));
        Assert.Equal(RefreshOutcome.Invalid, (await refresh.RotateAsync(next.Token)).Outcome);
    }

    [PostgresFact]
    public async Task Issuing_joins_the_ambient_transaction()
    {
        var refresh = Refresh();
        var p = Principal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            await refresh.IssueAsync(p);
            throw new InvalidOperationException("registration failed");
        }));
        Assert.Equal(0, await Count("it_refresh_tokens", $"principal_id = '{p}'"));
    }

    [PostgresFact]
    public async Task Mapped_column_names_are_used()
    {
        var refresh = Refresh(typeof(ItLegacySession));
        var p = Principal();
        var token = await refresh.IssueAsync(p);
        Assert.Equal(1, await Count("it_legacy_sessions", $"account_id = '{p}' AND secret_hash = '{OpaqueToken.Hash(token.Token)}'"));
        Assert.Equal(RefreshOutcome.Rotated, (await refresh.RotateAsync(token.Token)).Outcome);
        Assert.Equal(RefreshOutcome.Reused, (await refresh.RotateAsync(token.Token)).Outcome);
    }

    private OneTimeTokenStore<ItEmailLink> Links(Func<DateTime>? clock = null) => new(_db.Provider, null, clock);

    [PostgresFact]
    public async Task One_time_tokens_are_consumed_once_and_carry_their_payload()
    {
        var links = Links();
        var s = Principal();
        var token = await links.CreateAsync(s, "a@example.com", TimeSpan.FromHours(24));
        Assert.Equal(1, await Count("it_email_links", $"user_id = '{s}' AND email = 'a@example.com' AND token_hash = '{OpaqueToken.Hash(token)}'"));

        Assert.Equal(new OneTimeTokenResult(OneTimeTokenStatus.Ok, s, "a@example.com"), await links.ConsumeAsync(token));
        Assert.Equal(OneTimeTokenStatus.Used, (await links.ConsumeAsync(token)).Status);
        foreach (var junk in new[] { null, "", "garbage", OpaqueToken.New() })
            Assert.Equal(new OneTimeTokenResult(OneTimeTokenStatus.Invalid, null, null), await links.ConsumeAsync(junk));
    }

    [PostgresFact]
    public async Task A_new_token_retires_the_older_ones_and_expired_tokens_report_expired()
    {
        var now = DateTime.UtcNow;
        var links = Links(() => now);
        var s = Principal();
        var old = await links.CreateAsync(s, "a@example.com", TimeSpan.FromHours(1));
        var fresh = await links.CreateAsync(s, "b@example.com", TimeSpan.FromHours(1));
        Assert.Equal(OneTimeTokenStatus.Used, (await links.ConsumeAsync(old)).Status);

        now = now.AddHours(2);
        var expired = await links.ConsumeAsync(fresh);
        Assert.Equal(new OneTimeTokenResult(OneTimeTokenStatus.Expired, s, "b@example.com"), expired);

        await links.PruneAsync(now);
        Assert.Equal(2, await Count("it_email_links", $"user_id = '{s}'"));
        await links.PruneAsync(now.AddHours(25));
        Assert.Equal(0, await Count("it_email_links", $"user_id = '{s}'"));
    }

    [PostgresFact]
    public async Task Consumption_rolls_back_with_the_surrounding_transaction()
    {
        var links = Links();
        var token = await links.CreateAsync(Principal(), "c@example.com", TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Provider.InTransactionAsync<bool>(async _ =>
        {
            Assert.Equal(OneTimeTokenStatus.Ok, (await links.ConsumeAsync(token)).Status);
            throw new InvalidOperationException("account update failed");
        }));
        Assert.Equal(OneTimeTokenStatus.Ok, (await links.ConsumeAsync(token)).Status);
    }

    [PostgresFact]
    public async Task Registration_wires_the_stores_and_the_pruner()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISqlDatabaseProvider>(_db.Provider);
        TokenStoreConfiguration.Register(services, new[] { typeof(ItRefreshToken), typeof(ItEmailLink), typeof(ItRecord) });
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var refresh = sp.GetRequiredService<IRefreshTokenService>();
        var links = sp.GetRequiredService<IOneTimeTokenStore<ItEmailLink>>();
        Assert.Same(links, sp.GetRequiredService<IOneTimeTokenStore>());
        Assert.Equal(2, sp.GetServices<ITokenPruner>().Count());
        var pruner = Assert.Single(sp.GetServices<IHostedService>().OfType<TokenPruneService>());

        var p = Principal();
        var token = await refresh.IssueAsync(p);
        Assert.Equal(RefreshOutcome.Rotated, (await refresh.RotateAsync(token.Token)).Outcome);
        Assert.True(await pruner.PruneOnceAsync() >= 0);

        Assert.Same(refresh, sp.GetRequiredService<IRefreshTokenService<ItRefreshToken>>());

        // Several tables of a kind: each by its model, no unqualified service.
        var two = new ServiceCollection();
        two.AddLogging();
        two.AddSingleton<ISqlDatabaseProvider>(_db.Provider);
        TokenStoreConfiguration.Register(two, new[] { typeof(ItRefreshToken), typeof(ItLegacySession) });
        using (var both = two.BuildServiceProvider())
        {
            Assert.Null(both.GetService<IRefreshTokenService>());
            Assert.NotNull(both.GetService<IRefreshTokenService<ItLegacySession>>());
            Assert.Equal(2, both.GetServices<ITokenPruner>().Count());
        }

        var none = new ServiceCollection();
        TokenStoreConfiguration.Register(none, new[] { typeof(ItRecord) });
        Assert.Empty(none);
    }
}
