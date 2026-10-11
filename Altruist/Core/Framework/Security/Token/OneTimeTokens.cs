/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Data;

using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;

namespace Altruist.Security;

/// <summary>
/// One stored single-use token (only its hash): email verification links, password resets,
/// invitations. Declare a table per purpose by deriving a sealed class with a <c>[Vault]</c>
/// attribute; Altruist registers an <see cref="IOneTimeTokenStore{TModel}"/> for each:
/// <code>
/// [Vault("email_verifications", Keyspace: "myapp")]
/// public sealed class EmailVerificationVault : OneTimeTokenModel { }
/// </code>
/// Override a property with another <c>[VaultColumn]</c> name to use an existing table (see
/// <see cref="RefreshTokenModel"/>).
/// </summary>
[VaultUniqueKey(nameof(TokenHash))]
public abstract class OneTimeTokenModel : VaultModel
{
    /// <summary>Who or what the token acts on (an account id, an order id, ...).</summary>
    [VaultColumn("subject_id"), VaultColumnIndex]
    public virtual string SubjectId { get; set; } = "";

    /// <summary>Data bound to the token (e.g. the email address a link confirms), returned on consumption.</summary>
    [VaultColumn("payload", nullable: true)]
    public virtual string? Payload { get; set; }

    /// <summary><see cref="OpaqueToken.Hash"/> of the raw token (the raw token is only sent to the user).</summary>
    [VaultColumn("token_hash")]
    public virtual string TokenHash { get; set; } = "";

    /// <summary>UTC issue time.</summary>
    [VaultColumn("issued_at")]
    public virtual DateTime IssuedAt { get; set; }

    /// <summary>UTC expiry (issue time plus the ttl passed to <see cref="IOneTimeTokenStore.CreateAsync"/>).</summary>
    [VaultColumn("expires_at")]
    public virtual DateTime ExpiresAt { get; set; }

    /// <summary>When it was consumed or superseded by a newer token of the same subject; null while usable.</summary>
    [VaultColumn("used_at", nullable: true)]
    public virtual DateTime? UsedAt { get; set; }
}

/// <summary><c>altruist:security:one-time-tokens:expired-retention-hours</c> (24): how long expired tokens are kept (to answer "expired" rather than "invalid").</summary>
public sealed class OneTimeTokenOptions
{
    /// <summary>Config section of these options.</summary>
    public const string ConfigPath = "altruist:security:one-time-tokens";

    /// <summary>How long expired tokens are kept before pruning (default 24 hours).</summary>
    public TimeSpan ExpiredRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Reads <see cref="ConfigPath"/>; missing keys keep their defaults.</summary>
    public static OneTimeTokenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new OneTimeTokenOptions();
        if (TokenConfig.Number(configuration.GetSection(ConfigPath), "expired-retention-hours") is { } hours)
            options.ExpiredRetention = TimeSpan.FromHours(hours);
        return options;
    }
}

/// <summary>Result kind of <see cref="IOneTimeTokenStore.ConsumeAsync"/>.</summary>
public enum OneTimeTokenStatus
{
    /// <summary>Valid; it is now used.</summary>
    Ok,
    /// <summary>Unknown or malformed.</summary>
    Invalid,
    /// <summary>Known but past its expiry (until pruned after <see cref="OneTimeTokenOptions.ExpiredRetention"/>).</summary>
    Expired,
    /// <summary>Consumed before, or replaced by a newer token of the same subject.</summary>
    Used,
}

/// <summary>Outcome of <see cref="IOneTimeTokenStore.ConsumeAsync"/>. Act on the token only for <see cref="OneTimeTokenStatus.Ok"/>.</summary>
/// <param name="Status">What happened to the token.</param>
/// <param name="SubjectId">The token's subject (null for <see cref="OneTimeTokenStatus.Invalid"/>).</param>
/// <param name="Payload">The data stored with the token (null for <see cref="OneTimeTokenStatus.Invalid"/>).</param>
public sealed record OneTimeTokenResult(OneTimeTokenStatus Status, string? SubjectId, string? Payload);

/// <summary>
/// Single-use tokens stored hashed in SQL. Every call joins the ambient SQL transaction, so
/// consuming a token and acting on it (e.g. marking an email verified) commit or roll back together.
/// </summary>
/// <remarks>
/// Use it for links sent out of band (email verification, password reset, invitations): you choose the ttl per
/// call. For sign-in sessions use <see cref="IRefreshTokenService"/>; for WebSocket connects use
/// <see cref="IConnectionTicketService"/>. Registered (singleton) by <see cref="TokenStoreConfiguration"/> as
/// <see cref="IOneTimeTokenStore{TModel}"/> per <see cref="OneTimeTokenModel"/> vault, and also as this
/// interface when there is exactly one. Expired rows are pruned by <see cref="TokenPruneService"/>.
/// </remarks>
/// <example>
/// <code>
/// var raw = await verifications.CreateAsync(accountId, payload: email, ttl: TimeSpan.FromHours(24));
/// // email a link containing raw ...
/// var r = await verifications.ConsumeAsync(rawFromLink);
/// if (r.Status == OneTimeTokenStatus.Ok) { /* mark r.Payload verified for r.SubjectId */ }
/// </code>
/// </example>
public interface IOneTimeTokenStore : ITokenPruner
{
    /// <summary>
    /// Stores a token for <paramref name="subjectId"/> valid for <paramref name="ttl"/> and returns
    /// it raw (send it; it is not stored). Unused older tokens of the subject stop working.
    /// <paramref name="now"/> defaults to the current time.
    /// </summary>
    Task<string> CreateAsync(string subjectId, string? payload, TimeSpan ttl, DateTime? now = null, CancellationToken ct = default);

    /// <summary>Consumes <paramref name="rawToken"/> (atomic: concurrent consumers succeed once).</summary>
    Task<OneTimeTokenResult> ConsumeAsync(string? rawToken, DateTime? now = null, CancellationToken ct = default);
}

/// <summary>The store of one <see cref="OneTimeTokenModel"/> table (an application may have several).</summary>
public interface IOneTimeTokenStore<TModel> : IOneTimeTokenStore where TModel : OneTimeTokenModel
{
}

/// <summary>
/// <see cref="IOneTimeTokenStore{TModel}"/> over a SQL table (registered automatically for each
/// <see cref="OneTimeTokenModel"/> vault; construct it directly in tests).
/// </summary>
public sealed class OneTimeTokenStore<TModel> : IOneTimeTokenStore<TModel> where TModel : OneTimeTokenModel
{
    private readonly ISqlDatabaseProvider _db;
    private readonly ISqlTransactionProvider _tx;
    private readonly TokenTable _t;
    private readonly OneTimeTokenOptions _options;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Creates the store over the table of <typeparamref name="TModel"/>.</summary>
    /// <param name="db">A SQL provider that also implements <see cref="ISqlTransactionProvider"/>.</param>
    /// <param name="options">Retention settings (defaults when null).</param>
    /// <param name="utcNow">UTC clock (tests); default <see cref="DateTime.UtcNow"/>.</param>
    /// <exception cref="InvalidOperationException">When <paramref name="db"/> does not support transactions.</exception>
    public OneTimeTokenStore(ISqlDatabaseProvider db, OneTimeTokenOptions? options = null, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tx = db as ISqlTransactionProvider
              ?? throw new InvalidOperationException("One-time tokens need a SQL provider with transactions.");
        _t = TokenTable.Of(typeof(TModel), typeof(OneTimeTokenModel));
        _options = options ?? new OneTimeTokenOptions();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc/>
    public string PrunedName => $"expired {VaultDocument.From(typeof(TModel)).Name} tokens";

    private string C(string property) => _t.Column(property);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">When <paramref name="subjectId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="ttl"/> is not positive.</exception>
    public async Task<string> CreateAsync(string subjectId, string? payload, TimeSpan ttl, DateTime? now = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subjectId))
            throw new ArgumentException("A one-time token needs a subject.", nameof(subjectId));
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, "The lifetime must be positive.");
        var at = now ?? _utcNow();
        var raw = OpaqueToken.New();
        await _tx.InTransactionAsync(async _ =>
        {
            await _db.ExecuteAsync(
                $"UPDATE {_t.Table} SET {C(nameof(OneTimeTokenModel.UsedAt))} = ? WHERE {C(nameof(OneTimeTokenModel.SubjectId))} = ? AND {C(nameof(OneTimeTokenModel.UsedAt))} IS NULL",
                new List<object?> { at, subjectId }, ct).ConfigureAwait(false);
            var row = (OneTimeTokenModel)_t.New();
            row.SubjectId = subjectId;
            row.Payload = payload;
            row.TokenHash = OpaqueToken.Hash(raw);
            row.IssuedAt = at;
            row.ExpiresAt = at + ttl;
            return await _t.InsertAsync(_db, row, ct).ConfigureAwait(false);
        }, IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        return raw;
    }

    /// <inheritdoc/>
    public async Task<OneTimeTokenResult> ConsumeAsync(string? rawToken, DateTime? now = null, CancellationToken ct = default)
    {
        if (!OpaqueToken.IsWellFormed(rawToken))
            return new OneTimeTokenResult(OneTimeTokenStatus.Invalid, null, null);
        var hash = OpaqueToken.Hash(rawToken!);
        var at = now ?? _utcNow();
        return await _tx.InTransactionAsync(async _ =>
        {
            var row = (OneTimeTokenModel?)await _t.FindByHashForUpdateAsync(_db, hash, ct).ConfigureAwait(false);
            if (row is null)
                return new OneTimeTokenResult(OneTimeTokenStatus.Invalid, null, null);
            if (row.UsedAt is not null)
                return new OneTimeTokenResult(OneTimeTokenStatus.Used, row.SubjectId, row.Payload);
            if (TokenTable.Utc(row.ExpiresAt) <= at)
                return new OneTimeTokenResult(OneTimeTokenStatus.Expired, row.SubjectId, row.Payload);

            await _db.ExecuteAsync($"UPDATE {_t.Table} SET {C(nameof(OneTimeTokenModel.UsedAt))} = ? WHERE {_t.Id} = ?",
                new List<object?> { at, row.StorageId }, ct).ConfigureAwait(false);
            return new OneTimeTokenResult(OneTimeTokenStatus.Ok, row.SubjectId, row.Payload);
        }, IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes tokens that expired longer than <see cref="OneTimeTokenOptions.ExpiredRetention"/> ago.</summary>
    public Task<long> PruneAsync(DateTime now, CancellationToken ct = default) =>
        _db.ExecuteAsync($"DELETE FROM {_t.Table} WHERE {C(nameof(OneTimeTokenModel.ExpiresAt))} < ?",
            new List<object?> { now - _options.ExpiredRetention }, ct);
}
