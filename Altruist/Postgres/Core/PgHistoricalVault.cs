// PgHistoricalVault.cs (UPDATED) — FULL FILE
using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Postgres history reader behind <see cref="IVault{TVaultModel}.History"/>: queries the
/// <c>&lt;table&gt;_history</c> table (rows appended by <c>SaveAsync(..., saveHistory: true)</c>) within a
/// time window on its <c>timestamp</c> column. Uses the same WHERE / ORDER BY translation as
/// <see cref="PgVault{TVaultModel}"/>.
/// </summary>
internal sealed class PgHistoricalVault<TVaultModel> : SqlHistoricalVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    private readonly PgVault<TVaultModel> _ownerPg;

    /// <summary>Creates an unfiltered history query for <paramref name="owner"/>'s table.</summary>
    /// <param name="owner">The vault whose schema, table and column map are used.</param>
    public PgHistoricalVault(PgVault<TVaultModel> owner)
        : base(owner)
    {
        _ownerPg = owner;
    }

    private PgHistoricalVault(PgVault<TVaultModel> owner, QueryState state)
        : base(owner, state)
    {
        _ownerPg = owner;
    }

    /// <inheritdoc/>
    protected override SqlHistoricalVault<TVaultModel> Create(QueryState state)
        => new PgHistoricalVault<TVaultModel>(_ownerPg, state);

    /// <inheritdoc/>
    protected override string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, _ownerPg.VaultDocument);

    /// <inheritdoc/>
    protected override string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, _ownerPg.VaultDocument);

    /// <inheritdoc/>
    protected override string QuoteIdent(string ident)
        => $"\"{ident.Replace("\"", "\"\"")}\"";

    // If you want, override ToSqlLiteral to include timezone/UTC formatting specifics for PG.
}
