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
/// <remarks>
/// Reached through <see cref="IVault{TVaultModel}.History"/> on a model whose <see cref="Altruist.UORM.VaultAttribute"/> has
/// <c>StoreHistory: true</c>; history rows are appended only by saves called with <c>saveHistory: true</c>. Like
/// <see cref="SqlVault{TVaultModel}"/>, each fluent call returns a new immutable instance. Derive from it only when
/// writing a SQL provider.
/// </remarks>
/// <example>
/// <code>
/// var changes = await vault.History
///     .Where(x =&gt; x.StorageId == id)
///     .OrderByDescending(x =&gt; x.Version)
///     .ToListAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
/// </code>
/// </example>
/// <typeparam name="TVaultModel">The vault model type.</typeparam>
public abstract class SqlHistoricalVault<TVaultModel> : IHistoricalVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    /// <summary>The live vault this history belongs to (supplies keyspace, document and provider).</summary>
    protected readonly SqlVault<TVaultModel> Owner;
    /// <summary>The accumulated query state.</summary>
    protected readonly QueryState State;

    /// <summary>Creates a history vault with an empty query state.</summary>
    /// <param name="owner">The live vault.</param>
    protected SqlHistoricalVault(SqlVault<TVaultModel> owner)
        : this(owner, new QueryState())
    {
    }

    /// <summary>Creates a history vault carrying an existing query state.</summary>
    /// <param name="owner">The live vault.</param>
    /// <param name="state">Query state to carry.</param>
    protected SqlHistoricalVault(SqlVault<TVaultModel> owner, QueryState state)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Provider creates a new history vault instance with the given state.</summary>
    /// <param name="state">Query state.</param>
    /// <returns>A new history vault.</returns>
    protected abstract SqlHistoricalVault<TVaultModel> Create(QueryState state);

    // Provider hooks
    /// <summary>Translates a predicate to a SQL boolean expression.</summary>
    /// <param name="predicate">The predicate.</param>
    /// <returns>SQL fragment without WHERE.</returns>
    protected abstract string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate);
    /// <summary>Translates an order key to a SQL expression (the base appends <c>DESC</c> for descending).</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">The key selector.</param>
    /// <returns>SQL fragment.</returns>
    protected abstract string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>Quotes an identifier in the provider's dialect.</summary>
    /// <param name="ident">Identifier.</param>
    /// <returns>Quoted identifier.</returns>
    protected abstract string QuoteIdent(string ident);

    // ---------------- Query ops ----------------

    /// <summary>Returns a new history vault with an extra filter (AND-combined).</summary>
    /// <param name="predicate">Filter on model properties.</param>
    /// <returns>A new history vault.</returns>
    public IHistoricalVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        var where = ConvertWherePredicateToString(predicate);

        var next = State
            .With(QueryPosition.WHERE, where)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    /// <summary>Returns a new history vault that additionally sorts ascending by the key.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Property to sort by.</param>
    /// <returns>A new history vault.</returns>
    public IHistoricalVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = ConvertOrderByToString(keySelector);

        var next = State
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    /// <summary>Returns a new history vault that additionally sorts descending by the key.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Property to sort by.</param>
    /// <returns>A new history vault.</returns>
    public IHistoricalVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderBy = ConvertOrderByToString(keySelector) + " DESC";

        var next = State
            .With(QueryPosition.ORDER_BY, orderBy)
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    /// <summary>Returns a new history vault limited to <paramref name="count"/> rows. Call at most once per chain (LIMIT parts accumulate).</summary>
    /// <param name="count">Maximum rows.</param>
    /// <returns>A new history vault.</returns>
    public IHistoricalVault<TVaultModel> Take(int count)
    {
        var next = State
            .With(QueryPosition.LIMIT, $"LIMIT {count}")
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    /// <summary>Returns a new history vault that skips <paramref name="count"/> rows. Call at most once per chain.</summary>
    /// <param name="count">Rows to skip.</param>
    /// <returns>A new history vault.</returns>
    public IHistoricalVault<TVaultModel> Skip(int count)
    {
        var next = State
            .With(QueryPosition.OFFSET, $"OFFSET {count}")
            .EnsureProjectionSelected(Owner.VaultDocument);

        return Create(next);
    }

    // ---------------- Execution ----------------

    /// <summary>Returns history rows whose <c>timestamp</c> lies within [<paramref name="startTime"/>, <paramref name="endTime"/>] (inclusive) and that match the filters.</summary>
    /// <remarks>History timestamps are written as UTC; pass UTC bounds. Bounds are rendered with second precision.</remarks>
    /// <param name="startTime">Inclusive lower bound (UTC).</param>
    /// <param name="endTime">Inclusive upper bound (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The history rows.</returns>
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

    /// <summary>Renders a timestamp literal (<c>'yyyy-MM-dd HH:mm:ss'</c>, kind ignored); override for dialect-specific formats.</summary>
    /// <param name="dt">The value.</param>
    /// <returns>SQL literal.</returns>
    protected virtual string ToSqlLiteral(DateTime dt)
        => $"'{dt:yyyy-MM-dd HH:mm:ss}'";
}
