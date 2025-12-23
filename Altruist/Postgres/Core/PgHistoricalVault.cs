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

    public PgHistoricalVault(PgVault<TVaultModel> owner, QueryState state)
    {
        _owner = owner;
        _state = state;
    }

    private PgHistoricalVault<TVaultModel> New(QueryState st) => new PgHistoricalVault<TVaultModel>(_owner, st);

    public IHistoricalVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        var whereClause = _owner.ConvertWherePredicateToString(predicate);
        var next = _state
            .With(QueryPosition.WHERE, whereClause)
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderByClause = _owner.ConvertOrderByToString(keySelector);
        var next = _state
            .With(QueryPosition.ORDER_BY, orderByClause)
            .EnsureProjectionSelected(_owner.VaultDocument);

        return New(next);
    }

    public IHistoricalVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderByClause = _owner.ConvertOrderByDescendingToString(keySelector) + " DESC";
        var next = _state
            .With(QueryPosition.ORDER_BY, orderByClause)
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

    public async Task<List<TVaultModel>> ToListAsync(DateTime startTime, DateTime endTime)
    {
        var st = _state.EnsureProjectionSelected(_owner.VaultDocument);

        // Build SELECT projection like the main vault
        var select =
            st.Parts[QueryPosition.SELECT].Count == 0
                ? string.Join(", ", _owner.VaultDocument.Columns.Select(kvp =>
                    $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"))
                : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        var historyTableFqn =
            $"{QuoteIdent(_owner.Keyspace.Name)}.{QuoteIdent(_owner.VaultDocument.Name + "_history")}";

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(select)
           .Append(" FROM ").Append(historyTableFqn);

        // Existing WHERE chain from the historical query
        var existingWhere = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);

        var startLiteral = ConvertToSqlValue(startTime);
        var endLiteral = ConvertToSqlValue(endTime);

        var timeFilter =
            $"{QuoteIdent("timestamp")} >= {startLiteral} AND {QuoteIdent("timestamp")} <= {endLiteral}";

        string finalWhere;
        if (string.IsNullOrEmpty(existingWhere))
            finalWhere = timeFilter;
        else
            finalWhere = $"({existingWhere}) AND {timeFilter}";

        sql.Append(" WHERE ").Append(finalWhere);

        var orderBy = string.Join(", ", st.Parts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderBy))
            sql.Append(" ORDER BY ").Append(orderBy);

        var limit = string.Join(" ", st.Parts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limit))
            sql.Append(' ').Append(limit);

        var offset = string.Join(" ", st.Parts[QueryPosition.OFFSET]);
        if (!string.IsNullOrEmpty(offset))
            sql.Append(' ').Append(offset);

        // We inline literals here; no additional parameters at the moment.
        var rows = await _owner.DatabaseProvider
            .QueryAsync<TVaultModel>(sql.ToString(), parameters: null);

        return rows.ToList();
    }

    private static string QuoteIdent(string ident) =>
        $"\"{ident.Replace("\"", "\"\"")}\"";

    private static string ConvertToSqlValue(object? value) =>
        value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "TRUE" : "FALSE",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            TimeSpan ts => ConvertTimeSpanToDatabaseFormat(ts),
            Enum e => Convert.ToInt32(e).ToString(),
            _ => value.ToString()!
        };

    private static string ConvertTimeSpanToDatabaseFormat(TimeSpan ts)
    {
        var totalDays = (int)ts.TotalDays;
        var remainder = ts - TimeSpan.FromDays(totalDays);
        return $"'{totalDays} days {remainder:hh\\:mm\\:ss}'";
    }
}
