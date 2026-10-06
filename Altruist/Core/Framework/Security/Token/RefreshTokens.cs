/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Data;

using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Security;

/// <summary>
/// One stored refresh token (only its hash). Declare your table by deriving a sealed class with a
/// <c>[Vault]</c> attribute; Altruist then registers <see cref="IRefreshTokenService"/> on it:
/// <code>
/// [Vault("refresh_tokens", Keyspace: "myapp")]
/// public sealed class RefreshTokenVault : RefreshTokenModel { }
/// </code>
/// To use an existing table whose columns are named differently, override the property and give
/// it the column name: <c>[VaultColumn("account_id"), VaultColumnIndex] public override string PrincipalId { get; set; } = "";</c>
/// </summary>
[VaultUniqueKey(nameof(TokenHash))]
public abstract class RefreshTokenModel : VaultModel
{
    [VaultColumn("principal_id"), VaultColumnIndex]
    public virtual string PrincipalId { get; set; } = "";

    /// <summary>Every token descending from one sign-in shares a family; a replay revokes the family.</summary>
    [VaultColumn("family_id"), VaultColumnIndex]
    public virtual string FamilyId { get; set; } = "";

    /// <summary><see cref="OpaqueToken.Hash"/> of the raw token (the raw token is only ever with the client).</summary>
    [VaultColumn("token_hash")]
    public virtual string TokenHash { get; set; } = "";

    [VaultColumn("issued_at")]
    public virtual DateTime IssuedAt { get; set; }

    [VaultColumn("expires_at")]
    public virtual DateTime ExpiresAt { get; set; }

    /// <summary>When it was rotated (exchanged for its successor).</summary>
    [VaultColumn("used_at", nullable: true)]
    public virtual DateTime? UsedAt { get; set; }

    [VaultColumn("revoked_at", nullable: true)]
    public virtual DateTime? RevokedAt { get; set; }
}

/// <summary>
/// <c>altruist:security:refresh-tokens</c>: <c>lifetime-days</c> (30), <c>max-sessions</c> (10,
/// 0 = unlimited; the families <see cref="IRefreshTokenService.RevokeExcessAsync"/> keeps),
/// <c>used-retention-hours</c> (48: rotated tokens are kept this long so a replay is still detected)
/// and <c>revoked-retention-hours</c> (24).
/// </summary>
public sealed class RefreshTokenOptions
{
    public const string ConfigPath = "altruist:security:refresh-tokens";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);
    public int MaxSessions { get; set; } = 10;
    public TimeSpan UsedRetention { get; set; } = TimeSpan.FromHours(48);
    public TimeSpan RevokedRetention { get; set; } = TimeSpan.FromHours(24);

    public static RefreshTokenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new RefreshTokenOptions();
        if (TokenConfig.Number(section, "lifetime-days") is { } days)
            options.Lifetime = TimeSpan.FromDays(days);
        if (TokenConfig.Number(section, "max-sessions") is { } max)
            options.MaxSessions = (int)max;
        if (TokenConfig.Number(section, "used-retention-hours") is { } used)
            options.UsedRetention = TimeSpan.FromHours(used);
        if (TokenConfig.Number(section, "revoked-retention-hours") is { } revoked)
            options.RevokedRetention = TimeSpan.FromHours(revoked);
        return options;
    }
}

public enum RefreshOutcome
{
    /// <summary>The token was valid; it is now used and <see cref="RefreshRotation.Next"/> replaces it.</summary>
    Rotated,
    /// <summary>Unknown, malformed, expired or revoked.</summary>
    Invalid,
    /// <summary>Already rotated: someone replayed a stolen or stale token. Its whole family is revoked.</summary>
    Reused,
}

/// <summary>A new raw refresh token (give it to the client; it is not stored).</summary>
public sealed record IssuedRefreshToken(string Token, string FamilyId, DateTime ExpiresAt);

/// <param name="PrincipalId">The token's principal (also for <see cref="RefreshOutcome.Reused"/>), null when unknown.</param>
public sealed record RefreshRotation(RefreshOutcome Outcome, string? PrincipalId, IssuedRefreshToken? Next);

/// <summary>Stores that delete their stale rows periodically (<see cref="TokenPruneService"/>).</summary>
public interface ITokenPruner
{
    /// <summary>What is pruned, for the log ("refresh tokens").</summary>
    string PrunedName { get; }

    Task<long> PruneAsync(DateTime now, CancellationToken ct = default);
}

/// <summary>
/// Rotating refresh tokens with reuse detection. Each sign-in starts a family; every refresh
/// consumes the presented token and issues its successor in the same family. Presenting a token
/// that was already rotated revokes the family (the attacker's and the victim's sessions end).
/// Only hashes are stored, in the <see cref="RefreshTokenModel"/> table. Every call joins the
/// ambient SQL transaction (<see cref="ISqlTransactionProvider"/>), so a sign-in can create the
/// account and its first token atomically.
/// </summary>
public interface IRefreshTokenService : ITokenPruner
{
    RefreshTokenOptions Options { get; }

    /// <summary>A new token for <paramref name="principalId"/>: in <paramref name="familyId"/>, or a new family (a new session).</summary>
    Task<IssuedRefreshToken> IssueAsync(string principalId, string? familyId = null, CancellationToken ct = default);

    /// <summary>Consumes <paramref name="rawToken"/> and issues its successor (atomic: concurrent rotations of one token succeed once).</summary>
    Task<RefreshRotation> RotateAsync(string? rawToken, CancellationToken ct = default);

    /// <summary>Revokes the family of the presented token (sign-out). False for unknown tokens.</summary>
    Task<bool> RevokeFamilyAsync(string? rawToken, CancellationToken ct = default);

    /// <summary>Revokes every family of the principal (sign out everywhere).</summary>
    Task<long> RevokeAllAsync(string principalId, CancellationToken ct = default);

    /// <summary>
    /// Keeps the <paramref name="maxSessions"/> (default <see cref="RefreshTokenOptions.MaxSessions"/>)
    /// most recently active families of the principal and revokes the rest.
    /// </summary>
    Task<long> RevokeExcessAsync(string principalId, int? maxSessions = null, CancellationToken ct = default);
}

/// <summary>The refresh tokens of one <see cref="RefreshTokenModel"/> table (when an application has several).</summary>
public interface IRefreshTokenService<TModel> : IRefreshTokenService where TModel : RefreshTokenModel
{
}

/// <summary>
/// <see cref="IRefreshTokenService"/> over a SQL table (registered automatically for the
/// application's <see cref="RefreshTokenModel"/> vault; construct it directly in tests).
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    private readonly ISqlDatabaseProvider _db;
    private readonly ISqlTransactionProvider _tx;
    private readonly TokenTable _t;
    private readonly Func<DateTime> _utcNow;
    private readonly ILogger _log;

    public RefreshTokenService(
        ISqlDatabaseProvider db,
        Type modelType,
        RefreshTokenOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tx = db as ISqlTransactionProvider
              ?? throw new InvalidOperationException("Refresh tokens need a SQL provider with transactions.");
        _t = TokenTable.Of(modelType, typeof(RefreshTokenModel));
        Options = options ?? new RefreshTokenOptions();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RefreshTokenService>();
    }

    public RefreshTokenOptions Options { get; }

    public string PrunedName => "refresh tokens";

    private string C(string property) => _t.Column(property);

    public async Task<IssuedRefreshToken> IssueAsync(string principalId, string? familyId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(principalId))
            throw new ArgumentException("A refresh token needs a principal.", nameof(principalId));
        var now = _utcNow();
        var raw = OpaqueToken.New();
        var row = (RefreshTokenModel)_t.New();
        row.PrincipalId = principalId;
        row.FamilyId = string.IsNullOrEmpty(familyId) ? Guid.NewGuid().ToString("N") : familyId;
        row.TokenHash = OpaqueToken.Hash(raw);
        row.IssuedAt = now;
        row.ExpiresAt = now + Options.Lifetime;
        await _t.InsertAsync(_db, row, ct).ConfigureAwait(false);
        return new IssuedRefreshToken(raw, row.FamilyId, row.ExpiresAt);
    }

    public async Task<RefreshRotation> RotateAsync(string? rawToken, CancellationToken ct = default)
    {
        if (!OpaqueToken.IsWellFormed(rawToken))
            return new RefreshRotation(RefreshOutcome.Invalid, null, null);

        var hash = OpaqueToken.Hash(rawToken!);
        var result = await _tx.InTransactionAsync(async _ =>
        {
            var now = _utcNow();
            var row = (RefreshTokenModel?)await _t.FindByHashForUpdateAsync(_db, hash, ct).ConfigureAwait(false);
            if (row is null)
                return new RefreshRotation(RefreshOutcome.Invalid, null, null);

            if (row.UsedAt is not null || row.RevokedAt is not null)
            {
                await RevokeFamilyIdAsync(row.FamilyId, now, ct).ConfigureAwait(false);
                return new RefreshRotation(row.RevokedAt is null ? RefreshOutcome.Reused : RefreshOutcome.Invalid, row.PrincipalId, null);
            }

            if (TokenTable.Utc(row.ExpiresAt) <= now)
                return new RefreshRotation(RefreshOutcome.Invalid, row.PrincipalId, null);

            await _db.ExecuteAsync($"UPDATE {_t.Table} SET {C(nameof(RefreshTokenModel.UsedAt))} = ? WHERE {_t.Id} = ?",
                new List<object?> { now, row.StorageId }, ct).ConfigureAwait(false);
            var next = await IssueAsync(row.PrincipalId, row.FamilyId, ct).ConfigureAwait(false);
            return new RefreshRotation(RefreshOutcome.Rotated, row.PrincipalId, next);
        }, IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        if (result.Outcome == RefreshOutcome.Reused)
            _log.LogWarning("Refresh token reuse detected; token family revoked for principal {PrincipalId}", result.PrincipalId);
        return result;
    }

    public async Task<bool> RevokeFamilyAsync(string? rawToken, CancellationToken ct = default)
    {
        if (!OpaqueToken.IsWellFormed(rawToken))
            return false;
        var row = (RefreshTokenModel?)await _t.FindByHashAsync(_db, OpaqueToken.Hash(rawToken!), ct).ConfigureAwait(false);
        if (row is null)
            return false;
        await RevokeFamilyIdAsync(row.FamilyId, _utcNow(), ct).ConfigureAwait(false);
        return true;
    }

    public Task<long> RevokeAllAsync(string principalId, CancellationToken ct = default) =>
        _db.ExecuteAsync(
            $"UPDATE {_t.Table} SET {C(nameof(RefreshTokenModel.RevokedAt))} = ? WHERE {C(nameof(RefreshTokenModel.PrincipalId))} = ? AND {C(nameof(RefreshTokenModel.RevokedAt))} IS NULL",
            new List<object?> { _utcNow(), principalId }, ct);

    public Task<long> RevokeExcessAsync(string principalId, int? maxSessions = null, CancellationToken ct = default)
    {
        var max = maxSessions ?? Options.MaxSessions;
        if (max <= 0)
            return Task.FromResult(0L);
        var (principal, family, revoked, issued) = (C(nameof(RefreshTokenModel.PrincipalId)), C(nameof(RefreshTokenModel.FamilyId)),
            C(nameof(RefreshTokenModel.RevokedAt)), C(nameof(RefreshTokenModel.IssuedAt)));
        return _db.ExecuteAsync(
            $"UPDATE {_t.Table} SET {revoked} = ? WHERE {principal} = ? AND {revoked} IS NULL AND {family} NOT IN (" +
            $"SELECT {family} FROM {_t.Table} WHERE {principal} = ? AND {revoked} IS NULL GROUP BY {family} ORDER BY MAX({issued}) DESC LIMIT ?)",
            new List<object?> { _utcNow(), principalId, principalId, max }, ct);
    }

    /// <summary>
    /// Deletes expired tokens, tokens revoked longer than <see cref="RefreshTokenOptions.RevokedRetention"/>
    /// ago and tokens rotated longer than <see cref="RefreshTokenOptions.UsedRetention"/> ago (a replay
    /// of those is simply invalid). Every refresh adds a row, so without the last rule an active
    /// session would grow the table for its whole lifetime.
    /// </summary>
    public Task<long> PruneAsync(DateTime now, CancellationToken ct = default) =>
        _db.ExecuteAsync(
            $"DELETE FROM {_t.Table} WHERE {C(nameof(RefreshTokenModel.ExpiresAt))} < ? OR {C(nameof(RefreshTokenModel.RevokedAt))} < ? OR {C(nameof(RefreshTokenModel.UsedAt))} < ?",
            new List<object?> { now, now - Options.RevokedRetention, now - Options.UsedRetention }, ct);

    private Task<long> RevokeFamilyIdAsync(string familyId, DateTime now, CancellationToken ct) =>
        _db.ExecuteAsync(
            $"UPDATE {_t.Table} SET {C(nameof(RefreshTokenModel.RevokedAt))} = ? WHERE {C(nameof(RefreshTokenModel.FamilyId))} = ? AND {C(nameof(RefreshTokenModel.RevokedAt))} IS NULL",
            new List<object?> { now, familyId }, ct);
}

/// <summary><see cref="RefreshTokenService"/> on the table of <typeparamref name="TModel"/>.</summary>
public sealed class RefreshTokenService<TModel> : RefreshTokenService, IRefreshTokenService<TModel> where TModel : RefreshTokenModel
{
    public RefreshTokenService(ISqlDatabaseProvider db, RefreshTokenOptions? options = null, ILoggerFactory? loggerFactory = null, Func<DateTime>? utcNow = null)
        : base(db, typeof(TModel), options, loggerFactory, utcNow) { }
}

/// <summary>Table and column names of a token vault model, and the few SQL statements every token store shares.</summary>
internal sealed class TokenTable
{
    private readonly VaultDocument _doc;

    private TokenTable(VaultDocument doc)
    {
        _doc = doc;
        Table = doc.QualifiedTable();
        Id = Column(nameof(IVaultModel.StorageId));
        Select = string.Join(", ", doc.Fields.Select(f => $"{VaultDocument.Quote(doc.Columns[f])} AS {VaultDocument.Quote(f)}"));
    }

    public string Table { get; }
    public string Id { get; }
    /// <summary>"col" AS "Prop" for every column (raw SELECTs materialize by property name).</summary>
    public string Select { get; }
    public Type ModelType => _doc.Type;

    public static TokenTable Of(Type modelType, Type baseType)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        if (modelType.IsAbstract || !baseType.IsAssignableFrom(modelType))
            throw new ArgumentException($"{modelType.FullName} must be a concrete subclass of {baseType.Name}.", nameof(modelType));
        return new TokenTable(VaultDocument.From(modelType));
    }

    public string Column(string property) => VaultDocument.Quote(_doc.Columns[property]);

    public object New() => Activator.CreateInstance(_doc.Type)!;

    public Task<long> InsertAsync(ISqlDatabaseProvider db, IVaultModel row, CancellationToken ct)
    {
        row.OnSave();
        if (row.Version <= 0)
            row.Version = 1;
        var cols = _doc.Fields.Select(f => VaultDocument.Quote(_doc.Columns[f])).ToArray();
        var args = _doc.Fields.Select(f => _doc.PropertyAccessors[f](row)).ToList();
        return db.ExecuteAsync(
            $"INSERT INTO {Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", cols.Select(_ => "?"))})", args, ct);
    }

    public Task<object?> FindByHashAsync(ISqlDatabaseProvider db, string hash, CancellationToken ct) => FindAsync(db, hash, "", ct);

    /// <summary>Locks the row until the surrounding transaction ends (concurrent consumers wait, then see it used).</summary>
    public Task<object?> FindByHashForUpdateAsync(ISqlDatabaseProvider db, string hash, CancellationToken ct) => FindAsync(db, hash, " FOR UPDATE", ct);

    private async Task<object?> FindAsync(ISqlDatabaseProvider db, string hash, string suffix, CancellationToken ct)
    {
        var rows = await db.QueryAsync(_doc.Type, $"SELECT {Select} FROM {Table} WHERE {Column("TokenHash")} = ?{suffix}",
            new List<object?> { hash }, ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    /// <summary>Database timestamps are UTC "timestamp" columns; marks values read back as UTC.</summary>
    public static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

internal static class TokenConfig
{
    public static double? Number(IConfigurationSection section, string key)
    {
        var value = section[key] ?? section[key.Replace("-", "")];
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : null;
    }

    public static string? Text(IConfigurationSection section, string key)
    {
        var value = section[key] ?? section[key.Replace("-", "")];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool? Bool(IConfigurationSection section, string key) =>
        bool.TryParse(Text(section, key), out var b) ? b : null;
}
