namespace Altruist.Persistence;

/// <summary>
/// Immutable per-chain query state of the SQL vaults: filters, sort keys and the paging window, each kept in call
/// order. Every fluent call returns a new state with one piece added; the state it was called on is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="SqlVault{TVaultModel}"/> and <see cref="SqlHistoricalVault{TVaultModel}"/> providers; application
/// code does not build it.
/// </para>
/// <para>
/// Paging composes like LINQ's <c>Skip</c>/<c>Take</c> on the rows the earlier calls selected:
/// <c>Take(10).Take(3)</c> keeps 3 rows, <c>Skip(2).Skip(3)</c> skips 5, and <c>Take(10).Skip(4)</c> keeps rows 4..9 of
/// the first ten (6 rows). The window is therefore always one <see cref="Offset"/> plus an optional <see cref="Limit"/>,
/// rendered as a single <c>LIMIT</c>/<c>OFFSET</c> pair. Negative counts are treated as 0, as in LINQ.
/// </para>
/// </remarks>
public sealed class QueryState
{
    /// <summary>A state with no filter, no sort key and no paging.</summary>
    public static QueryState Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>(), 0, null);

    /// <summary>SQL boolean fragments, AND-combined, in call order.</summary>
    public IReadOnlyList<string> Filters { get; }

    /// <summary>SQL sort keys (<c>"col"</c> or <c>"col" DESC</c>), in call order: the first one is the primary key.</summary>
    public IReadOnlyList<string> OrderKeys { get; }

    /// <summary>Rows to skip before the window starts (0 when unpaged).</summary>
    public int Offset { get; }

    /// <summary>Maximum number of rows in the window, or <c>null</c> for no limit.</summary>
    public int? Limit { get; }

    /// <summary>True when <see cref="Offset"/> or <see cref="Limit"/> restricts the rows.</summary>
    public bool IsPaged => Offset > 0 || Limit is not null;

    private QueryState(IReadOnlyList<string> filters, IReadOnlyList<string> orderKeys, int offset, int? limit)
    {
        Filters = filters;
        OrderKeys = orderKeys;
        Offset = offset;
        Limit = limit;
    }

    /// <summary>Returns a new state with <paramref name="filter"/> AND-combined with the existing filters.</summary>
    /// <param name="filter">SQL boolean expression without the <c>WHERE</c> keyword.</param>
    /// <returns>The new state.</returns>
    public QueryState WithFilter(string filter)
        => new(Append(Filters, filter), OrderKeys, Offset, Limit);

    /// <summary>Returns a new state with <paramref name="orderKey"/> appended as the least significant sort key.</summary>
    /// <param name="orderKey">SQL sort key, e.g. <c>"rank" DESC</c>.</param>
    /// <returns>The new state.</returns>
    public QueryState WithOrderKey(string orderKey)
        => new(Filters, Append(OrderKeys, orderKey), Offset, Limit);

    /// <summary>Returns a new state that skips <paramref name="count"/> more rows of the current window (LINQ <c>Skip</c>).</summary>
    /// <param name="count">Rows to skip; negative counts as 0.</param>
    /// <returns>The new state.</returns>
    public QueryState Skip(int count)
    {
        var n = Math.Max(0, count);
        int? limit = Limit is { } l ? Math.Max(0, l - n) : null;
        return new(Filters, OrderKeys, checked(Offset + n), limit);
    }

    /// <summary>Returns a new state keeping at most <paramref name="count"/> rows of the current window (LINQ <c>Take</c>).</summary>
    /// <param name="count">Maximum rows; negative counts as 0.</param>
    /// <returns>The new state.</returns>
    public QueryState Take(int count)
    {
        var n = Math.Max(0, count);
        return new(Filters, OrderKeys, Offset, Limit is { } l ? Math.Min(l, n) : n);
    }

    /// <summary>The <c> WHERE ...</c> clause (with a leading space), or an empty string without filters.</summary>
    /// <returns>SQL text.</returns>
    public string WhereClause()
        => Filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", Filters.Select(f => $"({f})"));

    /// <summary>The <c> ORDER BY ...</c> clause (with a leading space), or an empty string without sort keys.</summary>
    /// <returns>SQL text.</returns>
    public string OrderByClause()
        => OrderKeys.Count == 0 ? "" : " ORDER BY " + string.Join(", ", OrderKeys);

    /// <summary>The <c> LIMIT n</c> / <c> OFFSET n</c> clauses (with leading spaces), or an empty string when unpaged.</summary>
    /// <returns>SQL text.</returns>
    public string PagingClause()
    {
        var sql = Limit is { } l ? $" LIMIT {l}" : "";
        return Offset > 0 ? sql + $" OFFSET {Offset}" : sql;
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> list, string item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        var copy = new string[list.Count + 1];
        for (var i = 0; i < list.Count; i++)
            copy[i] = list[i];
        copy[^1] = item;
        return copy;
    }
}
