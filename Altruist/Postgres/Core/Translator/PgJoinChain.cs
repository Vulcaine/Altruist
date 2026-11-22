/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;

using Altruist.Querying;

namespace Altruist.Persistence.Postgres.Querying;

/// <summary>One table of a join chain: its alias (<c>t0</c>, <c>t1</c>, ...) and column map.</summary>
/// <param name="Alias">Unquoted alias, unique within the chain.</param>
/// <param name="Document">Table metadata of the model.</param>
/// <param name="ModelType">The vault model type.</param>
internal sealed record PgJoinTable(string Alias, VaultDocument Document, Type ModelType);

/// <summary>
/// Immutable state and SQL builder shared by the join queries (<see cref="IVaultJoinQuery{T1, T2}"/> up to six
/// tables). Lambda parameter <c>i</c> of every predicate, key or projection refers to <see cref="Tables"/>[<c>i</c>];
/// each table is referenced through its own alias, so a model can appear more than once.
/// </summary>
/// <remarks>
/// The root is the filtered rows of the vault the chain started from: when that vault has filters, sort keys or
/// paging, the root becomes the derived table <c>(SELECT * FROM root WHERE ... ORDER BY ... LIMIT ...) AS "t0"</c>
/// (LINQ semantics: the selected root rows are joined), and its sort keys lead the join's ORDER BY.
/// </remarks>
internal sealed class PgJoinChain
{
    private readonly string _from;
    private readonly IReadOnlyList<string> _joins;

    /// <summary>The provider the statements run on (the root vault's).</summary>
    public ISqlDatabaseProvider Provider { get; }

    /// <summary>Tables in lambda-parameter order.</summary>
    public IReadOnlyList<PgJoinTable> Tables { get; }

    /// <summary>Qualified filters, sort keys and paging of the joined rows.</summary>
    public QueryState State { get; }

    private PgJoinChain(ISqlDatabaseProvider provider, IReadOnlyList<PgJoinTable> tables, string from,
        IReadOnlyList<string> joins, QueryState state)
    {
        Provider = provider;
        Tables = tables;
        _from = from;
        _joins = joins;
        State = state;
    }

    /// <summary>Starts a chain at <paramref name="root"/>, keeping the rows its query state selects.</summary>
    public static PgJoinChain Start<T>(PgVault<T> root) where T : class, IVaultModel
    {
        var table = new PgJoinTable("t0", root.VaultDocument, typeof(T));
        var alias = PgLiterals.Ident(table.Alias);
        var rootState = root.State;

        if (rootState.Filters.Count == 0 && rootState.OrderKeys.Count == 0 && !rootState.IsPaged)
            return new PgJoinChain(root.DatabaseProvider, [table], $"{root.TableSql} AS {alias}", [], QueryState.Empty);

        // Root sort keys are unqualified "col"[ DESC] fragments of the root table; the derived table exposes the
        // same column names under the root alias.
        var state = rootState.OrderKeys.Aggregate(QueryState.Empty, (st, key) => st.WithOrderKey($"{alias}.{key}"));
        return new PgJoinChain(root.DatabaseProvider, [table], $"({root.FilteredRowsSql()}) AS {alias}", [], state);
    }

    /// <summary>Adds <paramref name="right"/> joined on <c>leftKey(Tables[leftIndex]) = rightKey(right)</c>.</summary>
    /// <exception cref="InvalidOperationException">The chain already has paging.</exception>
    public PgJoinChain Join<TRight>(PgVault<TRight> right, int leftIndex, LambdaExpression leftKey,
        LambdaExpression rightKey, JoinType joinType)
        where TRight : class, IVaultModel
    {
        EnsureNotPaged("Join");

        var kind = joinType switch
        {
            JoinType.Inner => "INNER JOIN",
            JoinType.Left => "LEFT JOIN",
            JoinType.Right => "RIGHT JOIN",
            JoinType.Full => "FULL JOIN",
            _ => throw new ArgumentOutOfRangeException(nameof(joinType))
        };

        var rightTable = new PgJoinTable($"t{Tables.Count}", right.VaultDocument, typeof(TRight));
        var leftCol = PgPredicateTranslator.Column(leftKey.Body, Columns(leftKey, Tables[leftIndex]));
        var rightCol = PgPredicateTranslator.Column(rightKey.Body, Columns(rightKey, rightTable));
        var join = $"{kind} {right.TableSql} AS {PgLiterals.Ident(rightTable.Alias)} ON {leftCol} = {rightCol}";

        return new PgJoinChain(Provider, [.. Tables, rightTable], _from, [.. _joins, join], State);
    }

    /// <summary>Index of the only table of model <paramref name="modelType"/> (for <c>JoinFrom</c>).</summary>
    /// <exception cref="InvalidOperationException">The model is not in the chain, or appears more than once.</exception>
    public int IndexOf(Type modelType)
    {
        var matches = Tables.Select((t, i) => (t, i)).Where(x => x.t.ModelType == modelType).ToList();
        return matches.Count switch
        {
            1 => matches[0].i,
            0 => throw new InvalidOperationException(
                $"JoinFrom<{modelType.Name},...> requires {modelType.Name} to be part of the current join chain."),
            _ => throw new InvalidOperationException(
                $"JoinFrom<{modelType.Name},...> is ambiguous: {modelType.Name} appears {matches.Count} times in the chain. " +
                "Use Join (from the last table) or JoinFromLeft (from the root) instead.")
        };
    }

    /// <summary>Adds a filter over the joined tables.</summary>
    /// <exception cref="InvalidOperationException">The chain already has paging.</exception>
    public PgJoinChain Where(LambdaExpression predicate)
    {
        EnsureNotPaged("Where");
        return With(State.WithFilter(PgPredicateTranslator.Translate(predicate.Body, Columns(predicate))));
    }

    /// <summary>Appends a sort key taken from any table.</summary>
    /// <exception cref="InvalidOperationException">The chain already has paging.</exception>
    public PgJoinChain OrderBy(LambdaExpression keySelector, bool descending)
    {
        EnsureNotPaged(descending ? "OrderByDescending" : "OrderBy");
        var column = PgPredicateTranslator.Column(keySelector.Body, Columns(keySelector));
        return With(State.WithOrderKey(descending ? column + " DESC" : column));
    }

    /// <summary>Skips rows of the current window (LINQ semantics).</summary>
    public PgJoinChain Skip(int count) => With(State.Skip(count));

    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (LINQ semantics).</summary>
    public PgJoinChain Take(int count) => With(State.Take(count));

    /// <summary>Runs the chain and returns the root table's rows.</summary>
    public async Task<List<T>> RootRowsAsync<T>(QueryState state, CancellationToken ct)
    {
        var rows = await Provider.QueryAsync<T>(Select(RootProjection(), state), parameters: null, ct).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Runs the chain projected through <paramref name="selector"/>.</summary>
    public async Task<List<TResult>> SelectAsync<TResult>(LambdaExpression selector, CancellationToken ct)
    {
        var rows = await Provider.QueryAsync<TResult>(Select(Projection(selector), State), parameters: null, ct)
            .ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Counts the joined rows of the current window.</summary>
    public Task<long> CountAsync(CancellationToken ct)
    {
        var sql = State.IsPaged
            ? $"SELECT COUNT(*) FROM ({Select("1", State)}) AS q"
            : $"SELECT COUNT(*) FROM {Source()}{State.WhereClause()}";
        return Provider.ExecuteCountAsync(sql, parameters: null, ct);
    }

    private string Source() => _joins.Count == 0 ? _from : $"{_from} {string.Join(" ", _joins)}";

    private string Select(string projection, QueryState state)
        => $"SELECT {projection} FROM {Source()}{state.WhereClause()}{state.OrderByClause()}{state.PagingClause()}";

    private PgJoinChain With(QueryState state) => new(Provider, Tables, _from, _joins, state);

    private void EnsureNotPaged(string operation)
    {
        if (State.IsPaged)
            throw new InvalidOperationException(
                $"{operation} after Skip/Take is not supported: SQL joins, filters and sorts before paging. " +
                $"Call {operation} before Skip/Take.");
    }

    // ---------------- columns ----------------

    private static string ColumnSql(PgJoinTable table, string property)
        => $"{PgLiterals.Ident(table.Alias)}.{PgLiterals.Ident(PgQueryTranslator.ColumnOf(table.Document, property))}";

    private PgColumnResolver Columns(LambdaExpression lambda)
        => (parameter, member) =>
        {
            var index = lambda.Parameters.IndexOf(parameter);
            return index < 0 ? null : ColumnSql(Tables[index], member.Name);
        };

    private static PgColumnResolver Columns(LambdaExpression lambda, PgJoinTable table)
        => (parameter, member) => parameter == lambda.Parameters[0] ? ColumnSql(table, member.Name) : null;

    // ---------------- projections ----------------

    private string RootProjection() => AllColumns(Tables[0]);

    private static string AllColumns(PgJoinTable table)
        => string.Join(", ", table.Document.Columns.Select(kvp =>
            $"{PgLiterals.Ident(table.Alias)}.{PgLiterals.Ident(kvp.Value)} AS {PgLiterals.Ident(kvp.Key)}"));

    /// <summary>
    /// <c>(a, b) =&gt; b</c> (every column of that table), <c>new { X = a.P }</c> or <c>new T { X = a.P }</c> whose
    /// values are columns, constants or <c>??</c> coalesces.
    /// </summary>
    private string Projection(LambdaExpression selector)
    {
        var body = StripConvert(selector.Body);
        var columns = Columns(selector);

        return body switch
        {
            ParameterExpression p when selector.Parameters.IndexOf(p) is var i and >= 0 => AllColumns(Tables[i]),
            NewExpression { Members: not null } ne => string.Join(", ",
                ne.Arguments.Select((arg, i) => $"{Value(arg, columns)} AS {PgLiterals.Ident(ne.Members![i].Name)}")),
            MemberInitExpression init => string.Join(", ", init.Bindings.Select(b => b is MemberAssignment ma
                ? $"{Value(ma.Expression, columns)} AS {PgLiterals.Ident(ma.Member.Name)}"
                : throw new NotSupportedException("Only member assignments are supported in projections."))),
            _ => throw new NotSupportedException(
                "Projection must be 'new { ... }', 'new T { ... }' or a query parameter (e.g. (a, b) => a).")
        };
    }

    private static string Value(Expression expr, PgColumnResolver columns)
    {
        expr = StripConvert(expr);

        if (expr is ConstantExpression c)
            return PgLiterals.Format(c.Value);

        if (expr is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            var right = StripConvert(coalesce.Right);
            var left = Value(coalesce.Left, columns);
            return right is ConstantExpression { Value: null } ? left : $"COALESCE({left}, {Value(right, columns)})";
        }

        return PgPredicateTranslator.Column(expr, columns);
    }

    private static Expression StripConvert(Expression expr)
    {
        while (expr is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            expr = u.Operand;
        return expr;
    }
}
