using System.Linq.Expressions;
using System.Text;

namespace Altruist.Persistence.Postgres;

internal sealed class PgHistoricalVault<TVaultModel> : IHistoricalVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    private readonly PgVault<TVaultModel> _owner;
    private readonly QueryState _state;

    public PgHistoricalVault(PgVault<TVaultModel> owner)
        : this(owner, new QueryState())
    {
    }

    private PgHistoricalVault(PgVault<TVaultModel> owner, QueryState state)
    {
        _owner = owner;
        _state = state;
    }

    private PgHistoricalVault<TVaultModel> New(QueryState st)
        => new PgHistoricalVault<TVaultModel>(_owner, st);

    // ---------------- Query ops ----------------

    public IHistoricalVault<TVaultModel> Where(
        Expression<Func<TVaultModel, bool>> predicate)
    {
        var where = PgQueryTranslator.Where(predicate, _owner.VaultDocument);

        var next = _state
            .With(QueryPosition.WHERE, where)
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> OrderBy<TKey>(
        Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = PgQueryTranslator.OrderBy(keySelector, _owner.VaultDocument);

        var next = _state
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> OrderByDescending<TKey>(
        Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = PgQueryTranslator
            .OrderBy(keySelector, _owner.VaultDocument) + " DESC";

        var next = _state
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> Take(int count)
    {
        var next = _state
            .With(QueryPosition.LIMIT, $"LIMIT {count}")
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> Skip(int count)
    {
        var next = _state
            .With(QueryPosition.OFFSET, $"OFFSET {count}")
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    // ---------------- Execution ----------------

    public async Task<List<TVaultModel>> ToListAsync(
        DateTime startTime,
        DateTime endTime, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var st = _state.EnsureProjectionSelected(_owner.VaultDocument);

        var select =
            st.Parts[QueryPosition.SELECT].Count == 0
                ? string.Join(", ",
                    _owner.VaultDocument.Columns.Select(kvp =>
                        $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"))
                : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        var historyTable =
            $"{QuoteIdent(_owner.Keyspace.Name)}." +
            $"{QuoteIdent(_owner.VaultDocument.Name + "_history")}";

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(select)
           .Append(" FROM ").Append(historyTable);

        var existingWhere = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);

        var timeFilter =
            $"{QuoteIdent("timestamp")} >= {ToSql(startTime)} " +
            $"AND {QuoteIdent("timestamp")} <= {ToSql(endTime)}";

        var finalWhere = string.IsNullOrEmpty(existingWhere)
            ? timeFilter
            : $"({existingWhere}) AND {timeFilter}";

        sql.Append(" WHERE ").Append(finalWhere);

        if (st.Parts[QueryPosition.ORDER_BY].Count > 0)
            sql.Append(" ORDER BY ")
               .Append(string.Join(", ", st.Parts[QueryPosition.ORDER_BY]));

        if (st.Parts[QueryPosition.LIMIT].Count > 0)
            sql.Append(' ')
               .Append(string.Join(" ", st.Parts[QueryPosition.LIMIT]));

        if (st.Parts[QueryPosition.OFFSET].Count > 0)
            sql.Append(' ')
               .Append(string.Join(" ", st.Parts[QueryPosition.OFFSET]));

        var rows = await _owner.DatabaseProvider
            .QueryAsync<TVaultModel>(sql.ToString(), parameters: null);

        return rows.ToList();
    }

    // ---------------- Helpers ----------------

    private static string QuoteIdent(string ident)
        => $"\"{ident.Replace("\"", "\"\"")}\"";

    private static string ToSql(object value) => value switch
    {
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        _ => throw new NotSupportedException("Unsupported history literal.")
    };
}
