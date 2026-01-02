// PgHistoricalVault.cs (UPDATED) — FULL FILE
using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

internal sealed class PgHistoricalVault<TVaultModel> : SqlHistoricalVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    private readonly PgVault<TVaultModel> _ownerPg;

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

    protected override SqlHistoricalVault<TVaultModel> Create(QueryState state)
        => new PgHistoricalVault<TVaultModel>(_ownerPg, state);

    protected override string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, _ownerPg.VaultDocument);

    protected override string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, _ownerPg.VaultDocument);

    protected override string QuoteIdent(string ident)
        => $"\"{ident.Replace("\"", "\"\"")}\"";

    // If you want, override ToSqlLiteral to include timezone/UTC formatting specifics for PG.
}
