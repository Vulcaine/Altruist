// SqlHistoricalVault.cs (NEW) — FULL FILE
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Linq.Expressions;
using System.Text;

namespace Altruist.Persistence;

/// <summary>
/// Base historical vault for SQL providers. Keeps fluent QueryState and builds a history SELECT.
/// Provider-specific vaults implement predicate/order translation and quoting.
/// </summary>
public abstract class SqlHistoricalVault<TVaultModel> : IHistoricalVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    protected readonly SqlVault<TVaultModel> Owner;
    protected readonly QueryState State;

    protected SqlHistoricalVault(SqlVault<TVaultModel> owner)
        : this(owner, new QueryState())
    {
    }

    protected SqlHistoricalVault(SqlVault<TVaultModel> owner, QueryState state)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    protected abstract SqlHistoricalVault<TVaultModel> Create(QueryState state);

    // Provider hooks
    protected abstract string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate);
    protected abstract string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    protected abstract string QuoteIdent(string ident);

    // ---------------- Query ops ----------------

    public IHistoricalVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        var where = ConvertWherePredicateToString(predicate);

        var next = State
            .With(QueryPosition.WHERE, where)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    public IHistoricalVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = ConvertOrderByToString(keySelector);

        var next = State
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    public IHistoricalVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = ConvertOrderByToString(keySelector) + " DESC";

        var next = State
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    public IHistoricalVault<TVaultModel> Take(int count)
    {
        var next = State
            .With(QueryPosition.LIMIT, $"LIMIT {count}")
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    public IHistoricalVault<TVaultModel> Skip(int count)
    {
        var next = State
            .With(QueryPosition.OFFSET, $"OFFSET {count}")
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    // ---------------- Execution ----------------

    public virtual async Task<List<TVaultModel>> ToListAsync(
        DateTime startTime,
        DateTime endTime,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var st = State.EnsureProjectionSelected(Owner.VaultDocument);

        var select =
            st.Parts[QueryPosition.SELECT].Count == 0
                ? string.Join(", ",
                    Owner.VaultDocument.Columns.Select(kvp =>
                        $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"))
                : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        var historyTable =
            $"{QuoteIdent(Owner.Keyspace.Name)}.{QuoteIdent(Owner.VaultDocument.Name + "_history")}";

        var sql = new StringBuilder(256);
        sql.Append("SELECT ").Append(select)
           .Append(" FROM ").Append(historyTable);

        var existingWhere = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);

        var timeFilter =
            $"{QuoteIdent("timestamp")} >= {ToSqlLiteral(startTime)} " +
            $"AND {QuoteIdent("timestamp")} <= {ToSqlLiteral(endTime)}";

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

        var rows = await Owner.DatabaseProvider
            .QueryAsync<TVaultModel>(sql.ToString(), parameters: null, ct)
            .ConfigureAwait(false);

        return rows.ToList();
    }

    // ---------------- Helpers ----------------

    protected virtual string ToSqlLiteral(DateTime dt)
        => $"'{dt:yyyy-MM-dd HH:mm:ss}'";
}
