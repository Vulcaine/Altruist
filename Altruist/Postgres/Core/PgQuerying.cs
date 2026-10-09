// PgVaultQuery.cs
using System.Linq.Expressions;
using System.Reflection;

using Altruist.Persistence.Postgres.Querying;
using Altruist.Querying;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Postgres entry point of the querying layer (<see cref="IVaultQuery"/>): starts a query on one vault table and
/// lets it be <c>Join</c>ed to up to five more vault tables (6 tables total), filtered, ordered, paged and projected.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton <see cref="IVaultQuery"/> when <c>altruist:persistence:database:provider</c> is
/// <c>postgres</c>. Choose it over a plain <c>IVault&lt;T&gt;</c> when the query spans several tables (SQL
/// <c>JOIN</c>); choose prefabs (<see cref="IPrefabs"/>) when you want an aggregate root hydrated with its related
/// rows as objects; drop to raw SQL on <see cref="ISqlDatabaseProvider"/> for aggregates, grouping or functions.
/// </para>
/// <para>
/// Single-table queries delegate to the underlying <see cref="PgVault{TVaultModel}"/> (same translation rules).
/// Join queries translate their own predicates: arbitrary trees of <c>&amp;&amp;</c>, <c>||</c> and comparison operators
/// whose leaves are columns of any joined table or client-evaluated values; column-to-column comparisons are
/// allowed here. Note that <c>x.Col == null</c> is emitted as <c>= NULL</c> (never true) in join predicates.
/// Join keys, order keys and projected members must be plain member accesses; projections may be
/// <c>(a, b) =&gt; a</c>, <c>new { ... }</c> or <c>new Dto { ... }</c> with members, constants and <c>??</c>.
/// Other shapes throw <see cref="NotSupportedException"/>. Values are inlined as escaped literals.
/// </para>
/// <para>
/// Filters added with <c>Where</c> on the single-table query <i>before</i> <c>Join</c> are not carried into the join
/// query; add filters after joining. None of these methods accept a cancellation token.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // rows of OrderVault joined to their owner, newest first
/// var rows = await query.From&lt;OrderVault&gt;()
///     .Join&lt;OwnerVault&gt;(o =&gt; o.OwnerId, w =&gt; w.StorageId)
///     .Where((o, w) =&gt; w.Region == region &amp;&amp; o.Total &gt; 100)
///     .OrderByDescending((o, w) =&gt; o.Timestamp)
///     .Take(50)
///     .SelectAsync((o, w) =&gt; new OrderRow { OrderId = o.StorageId, OwnerName = w.Name });
/// </code>
/// </example>
[Service(typeof(IVaultQuery))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgVaultQuery : IVaultQuery
{
    /// <inheritdoc/>
    /// <remarks>Resolves <c>IVault&lt;T&gt;</c> from the global container.</remarks>
    /// <exception cref="InvalidOperationException">The resolved vault is not a <see cref="PgVault{TVaultModel}"/>.</exception>
    public IVaultQuery<T> From<T>() where T : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<T>>();

        if (vault is not PgVault<T> pgVault)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<T>).Name}' as '{typeof(PgVault<T>).Name}', " +
                $"but got '{vault.GetType().Name}'.");
        }

        return new PgVaultQuery<T>(pgVault);
    }
}

/// <summary>Single-table query; a thin immutable wrapper over <see cref="PgVault{TVaultModel}"/>.</summary>
internal sealed class PgVaultQuery<T> : IVaultQuery<T>
    where T : class, IVaultModel
{
    internal readonly PgVault<T> Vault;

    /// <summary>Wraps <paramref name="vault"/> (its current query state is used as-is).</summary>
    /// <param name="vault">The Postgres vault to query.</param>
    public PgVaultQuery(PgVault<T> vault) => Vault = vault;

    /// <inheritdoc/>
    public IVaultQuery<T> Where(Expression<Func<T, bool>> predicate)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Where(predicate));

    /// <inheritdoc/>
    public IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderBy(keySelector));

    /// <inheritdoc/>
    public IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderByDescending(keySelector));

    /// <inheritdoc/>
    public IVaultQuery<T> Skip(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Skip(count));

    /// <inheritdoc/>
    public IVaultQuery<T> Take(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T, T2> Join<T2>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<T2, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T2 : class, IVaultModel
    {
        var rightVault = ResolvePgVault<T2>();

        var joins = new List<string>
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(
                left: Vault,
                right: rightVault,
                leftKey: leftKey,
                rightKey: rightKey,
                joinType: joinType)
        };

        return new PgVaultJoinQuery<T, T2>(
            root: Vault,
            t2: rightVault,
            joins: joins,
            wheres: [],
            vaults: new Dictionary<Type, object>
            {
                { typeof(T), Vault },
                { typeof(T2), rightVault }
            },
            orderBys: [],
            skip: null,
            take: null);
    }

    /// <inheritdoc/>
    public Task<List<T>> ToListAsync() => Vault.ToListAsync();
    /// <inheritdoc/>
    public Task<T?> FirstOrDefaultAsync() => Vault.FirstOrDefaultAsync();
    /// <inheritdoc/>
    public Task<long> CountAsync() => Vault.CountAsync();

    /// <inheritdoc/>
    public Task SaveAsync(T entity, bool? saveHistory = false)
        => Vault.SaveAsync(entity, saveHistory);

    /// <inheritdoc/>
    public Task SaveBatchAsync(IEnumerable<T> entities, bool? saveHistory = false)
        => Vault.SaveBatchAsync(entities, saveHistory);

    private static PgVault<TV> ResolvePgVault<TV>() where TV : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<TV>>();
        if (vault is not PgVault<TV> pg)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TV>).Name}' as '{typeof(PgVault<TV>).Name}', " +
                $"but got '{vault.GetType().Name}'.");
        }
        return pg;
    }
}

/// <summary>SQL helpers shared by the join query classes (ORDER BY extraction, LIMIT/OFFSET, COUNT wrapping).</summary>
internal static class PgJoinQuerySql
{
    /// <summary>Resolves <c>(a, b, ...) =&gt; x.Prop</c> to a fully qualified column (no direction).</summary>
    /// <exception cref="NotSupportedException">Not a direct member access on one of the query parameters.</exception>
    public static string ExtractOrderBySql(
     LambdaExpression keySelector,
     IReadOnlyDictionary<ParameterExpression, object> map)
    {
        static Expression Strip(Expression e)
        {
            while (e is UnaryExpression u &&
                   (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                e = u.Operand;
            return e;
        }

        static string QualifiedTable(object vault)
        {
            dynamic v = vault;
            return $"\"{v.Keyspace.Name}\".\"{v.VaultDocument.Name}\"";
        }

        static VaultDocument GetDoc(object vault)
        {
            dynamic v = vault;
            return (VaultDocument)v.VaultDocument;
        }

        var body = Strip(keySelector.Body);

        if (body is not MemberExpression m)
            throw new NotSupportedException(
                $"OrderBy expression '{keySelector}' is not supported. Use simple member access like (a,b)=>a.Prop or (a,b)=>b.Prop.");

        var target = Strip(m.Expression!);

        if (target is not ParameterExpression p)
            throw new NotSupportedException(
                $"OrderBy expression '{keySelector}' is not supported. Use direct member access on a query parameter.");

        if (!map.TryGetValue(p, out var vault))
            throw new NotSupportedException("OrderBy parameter must be one of the query parameters.");

        var doc = GetDoc(vault);
        var col = doc.Columns.TryGetValue(m.Member.Name, out var c)
            ? c
            : VaultDocument.ToCamelCase(m.Member.Name);

        return $"{QualifiedTable(vault)}.\"{col}\"";
    }

    private static string? TryGetAlias(object vault)
    {
        // Try common property names first
        var t = vault.GetType();

        string? GetStringMember(string name)
        {
            var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (prop is not null && prop.PropertyType == typeof(string))
                return (string?)prop.GetValue(vault);

            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field is not null && field.FieldType == typeof(string))
                return (string?)field.GetValue(vault);

            return null;
        }

        return
            GetStringMember("Alias") ??
            GetStringMember("SqlAlias") ??
            GetStringMember("TableAlias") ??
            GetStringMember("QueryAlias") ??
            GetStringMember("_alias") ??
            GetStringMember("_sqlAlias") ??
            GetStringMember("_tableAlias");
    }

    /// <summary>Appends <c>ORDER BY</c>, <c>LIMIT</c> and (when &gt; 0) <c>OFFSET</c> to a select statement.</summary>
    public static string ApplyOrderSkipTake(string sql, List<string> orderBys, int? skip, int? take)
    {
        if (orderBys.Count > 0)
            sql += " ORDER BY " + string.Join(", ", orderBys);

        // Postgres: LIMIT then OFFSET (or OFFSET alone)
        if (take is not null)
            sql += $" LIMIT {take.Value}";

        if (skip is not null && skip.Value > 0)
            sql += $" OFFSET {skip.Value}";

        return sql;
    }

    /// <summary>Runs <c>SELECT COUNT(*) FROM (&lt;select&gt;) AS q</c> on the root vault's provider.</summary>
    public static async Task<long> ExecCountAsync<T>(PgVault<T> root, string baseSelectSql)
        where T : class, IVaultModel
    {
        var countSql = $"SELECT COUNT(*) FROM ({baseSelectSql}) AS q";
        var res = await root.DatabaseProvider.QueryAsync<long>(countSql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <summary>Returns the order list plus one more key, newline-joined. Currently unused.</summary>
    public static string AddOrder(List<string> current, string column, bool desc)
    {
        var next = new List<string>(current)
        {
            $"{column} {(desc ? "DESC" : "ASC")}"
        };
        return string.Join("\n", next);
    }
}

// ---------------- Join query: 1 join ----------------

/// <summary>
/// Immutable join query over 2 tables. The 3- to 6-table variants below follow the same pattern: every operator
/// copies the join/where/order lists into a new instance; terminal calls build one SQL statement and run it on the
/// root vault's provider (<see cref="SqlVault{TVaultModel}.DatabaseProvider"/>).
/// </summary>
internal sealed class PgVaultJoinQuery<T1, T2> : IVaultJoinQuery<T1, T2>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    private readonly List<string> _orderBys;
    private readonly int? _skip;
    private readonly int? _take;

    /// <summary>Creates a join query from already-translated parts (used by the fluent operators).</summary>
    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults,
        List<string> orderBys,
        int? skip,
        int? take)
    {
        _root = root;
        _t2 = t2;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
        _orderBys = orderBys;
        _skip = skip;
        _take = take;
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: false);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T1, T2, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: true);

    private IVaultJoinQuery<T1, T2> OrderByInternal(LambdaExpression keySelector, bool desc)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { keySelector.Parameters[0], _root },
            { keySelector.Parameters[1], _t2 }
        };

        var colSql = PgJoinQuerySql.ExtractOrderBySql(keySelector, map);
        var order = new List<string>(_orderBys) { $"{colSql} {(desc ? "DESC" : "ASC")}" };

        return new PgVaultJoinQuery<T1, T2>(
            _root, _t2,
            [.. _joins],
            [.. _wheres],
            new Dictionary<Type, object>(_vaults),
            order, _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Skip(int count)
        => new PgVaultJoinQuery<T1, T2>(_root, _t2, [.. _joins], [.. _wheres],
            new Dictionary<Type, object>(_vaults), [.. _orderBys], Math.Max(0, count), _take);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Take(int count)
        => new PgVaultJoinQuery<T1, T2>(_root, _t2, [.. _joins], [.. _wheres],
            new Dictionary<Type, object>(_vaults), [.. _orderBys], _skip, Math.Max(0, count));

    /// <inheritdoc/>
    public async Task<List<T1>> ToListAsync()
    {
        Expression<Func<T1, T2, T1>> selector = (a, b) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync()
    {
        Expression<Func<T1, T2, T1>> selector = (a, b) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, take: 1);

        var res = await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync()
    {
        Expression<Func<T1, T2, T1>> selector = (a, b) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 }
        };

        var baseSql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return await PgJoinQuerySql.ExecCountAsync(_root, baseSql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Join<T3>(
        Expression<Func<T2, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel
    {
        var t3 = ResolvePgVault<T3>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_t2, t3, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T3), t3 }
        };

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> JoinFromLeft<T3>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel
    {
        var t3 = ResolvePgVault<T3>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_root, t3, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T3), t3 }
        };

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> JoinFrom<TFrom, T3>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T3 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t3 = ResolvePgVault<T3>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(from, t3, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T3), t3 }
        };

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };
        return new PgVaultJoinQuery<T1, T2>(_root, _t2, [.. _joins], wheres, new Dictionary<Type, object>(_vaults), [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql).ConfigureAwait(false)).ToList();
    }

    private object GetVault<TV>() where TV : class, IVaultModel
    {
        if (!_vaults.TryGetValue(typeof(TV), out var v))
            throw new InvalidOperationException($"JoinFrom<{typeof(TV).Name},...> requires {typeof(TV).Name} to be part of the current join chain.");
        return v;
    }

    private static PgVault<TV> ResolvePgVault<TV>() where TV : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<TV>>();
        if (vault is not PgVault<TV> pg)
            throw new InvalidOperationException($"Expected PgVault<{typeof(TV).Name}> from DI but got {vault.GetType().Name}.");
        return pg;
    }
}

// ---------------- Join query: 2 joins ----------------

internal sealed class PgVaultJoinQuery<T1, T2, T3> : IVaultJoinQuery<T1, T2, T3>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;
    private readonly PgVault<T3> _t3;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    private readonly List<string> _orderBys;
    private readonly int? _skip;
    private readonly int? _take;

    /// <summary>Creates a join query from already-translated parts (used by the fluent operators).</summary>
    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        PgVault<T3> t3,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults,
        List<string> orderBys,
        int? skip,
        int? take)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
        _orderBys = orderBys;
        _skip = skip;
        _take = take;
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector)
        => OrderByInternal(keySelector, false);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector)
        => OrderByInternal(keySelector, true);

    private IVaultJoinQuery<T1, T2, T3> OrderByInternal(LambdaExpression keySelector, bool desc)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { keySelector.Parameters[0], _root },
            { keySelector.Parameters[1], _t2 },
            { keySelector.Parameters[2], _t3 }
        };

        var colSql = PgJoinQuerySql.ExtractOrderBySql(keySelector, map);
        var order = new List<string>(_orderBys) { $"{colSql} {(desc ? "DESC" : "ASC")}" };

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, _t3, [.. _joins], [.. _wheres], new Dictionary<Type, object>(_vaults), order, _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Skip(int count)
        => new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, _t3, [.. _joins], [.. _wheres],
            new Dictionary<Type, object>(_vaults), [.. _orderBys], Math.Max(0, count), _take);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Take(int count)
        => new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, _t3, [.. _joins], [.. _wheres],
            new Dictionary<Type, object>(_vaults), [.. _orderBys], _skip, Math.Max(0, count));

    /// <inheritdoc/>
    public async Task<List<T1>> ToListAsync()
    {
        Expression<Func<T1, T2, T3, T1>> selector = (a, b, c) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync()
    {
        Expression<Func<T1, T2, T3, T1>> selector = (a, b, c) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, take: 1);

        var res = await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync()
    {
        Expression<Func<T1, T2, T3, T1>> selector = (a, b, c) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 }
        };

        var baseSql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return await PgJoinQuerySql.ExecCountAsync(_root, baseSql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Join<T4>(
        Expression<Func<T3, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel
    {
        var t4 = ResolvePgVault<T4>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_t3, t4, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T4), t4 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> JoinFromLeft<T4>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel
    {
        var t4 = ResolvePgVault<T4>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_root, t4, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T4), t4 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> JoinFrom<TFrom, T4>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T4 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t4 = ResolvePgVault<T4>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(from, t4, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T4), t4 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, [.. _wheres], vaults, [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 },
            { predicate.Parameters[2], _t3 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };
        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, _t3, [.. _joins], wheres, new Dictionary<Type, object>(_vaults), [.. _orderBys], _skip, _take);
    }

    /// <inheritdoc/>
    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql).ConfigureAwait(false)).ToList();
    }

    private object GetVault<TV>() where TV : class, IVaultModel
    {
        if (!_vaults.TryGetValue(typeof(TV), out var v))
            throw new InvalidOperationException($"JoinFrom<{typeof(TV).Name},...> requires {typeof(TV).Name} to be part of the current join chain.");
        return v;
    }

    private static PgVault<TV> ResolvePgVault<TV>() where TV : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<TV>>();
        if (vault is not PgVault<TV> pg)
            throw new InvalidOperationException($"Expected PgVault<{typeof(TV).Name}> from DI but got {vault.GetType().Name}.");
        return pg;
    }
}
// ---------------- Join query: 3 joins ----------------

internal sealed class PgVaultJoinQuery<T1, T2, T3, T4> : IVaultJoinQuery<T1, T2, T3, T4>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;
    private readonly PgVault<T3> _t3;
    private readonly PgVault<T4> _t4;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    private readonly List<string> _orderBys;
    private readonly int? _skip;
    private readonly int? _take;

    /// <summary>Creates a join query from already-translated parts (used by the fluent operators).</summary>
    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        PgVault<T3> t3,
        PgVault<T4> t4,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults,
        List<string> orderBys,
        int? skip,
        int? take)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _t4 = t4;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
        _orderBys = orderBys;
        _skip = skip;
        _take = take;
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: false);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: true);

    private IVaultJoinQuery<T1, T2, T3, T4> OrderByInternal(LambdaExpression keySelector, bool desc)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { keySelector.Parameters[0], _root },
            { keySelector.Parameters[1], _t2 },
            { keySelector.Parameters[2], _t3 },
            { keySelector.Parameters[3], _t4 }
        };

        var colSql = PgJoinQuerySql.ExtractOrderBySql(keySelector, map);
        var order = new List<string>(_orderBys) { $"{colSql} {(desc ? "DESC" : "ASC")}" };

        return new PgVaultJoinQuery<T1, T2, T3, T4>(
            _root, _t2, _t3, _t4,
            [.. _joins],
            [.. _wheres],
            new Dictionary<Type, object>(_vaults),
            order, _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Skip(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4>(
            _root, _t2, _t3, _t4,
            [.. _joins],
            [.. _wheres],
            new Dictionary<Type, object>(_vaults),
            [.. _orderBys],
            Math.Max(0, count),
            _take);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Take(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4>(
            _root, _t2, _t3, _t4,
            [.. _joins],
            [.. _wheres],
            new Dictionary<Type, object>(_vaults),
            [.. _orderBys],
            _skip,
            Math.Max(0, count));

    /// <inheritdoc/>
    public async Task<List<T1>> ToListAsync()
    {
        Expression<Func<T1, T2, T3, T4, T1>> selector = (a, b, c, d) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync()
    {
        Expression<Func<T1, T2, T3, T4, T1>> selector = (a, b, c, d) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, take: 1);

        var res = await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync()
    {
        Expression<Func<T1, T2, T3, T4, T1>> selector = (a, b, c, d) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 }
        };

        var baseSql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return await PgJoinQuerySql.ExecCountAsync(_root, baseSql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Join<T5>(
        Expression<Func<T4, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
    {
        var t5 = ResolvePgVault<T5>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_t4, t5, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T5), t5 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, t5,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFromLeft<T5>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
    {
        var t5 = ResolvePgVault<T5>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_root, t5, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T5), t5 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, t5,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFrom<TFrom, T5>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T5 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t5 = ResolvePgVault<T5>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(from, t5, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T5), t5 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, t5,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 },
            { predicate.Parameters[2], _t3 },
            { predicate.Parameters[3], _t4 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };

        return new PgVaultJoinQuery<T1, T2, T3, T4>(
            _root, _t2, _t3, _t4,
            [.. _joins],
            wheres,
            new Dictionary<Type, object>(_vaults),
            [.. _orderBys],
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql).ConfigureAwait(false)).ToList();
    }

    private object GetVault<TV>() where TV : class, IVaultModel
    {
        if (!_vaults.TryGetValue(typeof(TV), out var v))
            throw new InvalidOperationException($"JoinFrom<{typeof(TV).Name},...> requires {typeof(TV).Name} to be part of the current join chain.");
        return v;
    }

    private static PgVault<TV> ResolvePgVault<TV>() where TV : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<TV>>();
        if (vault is not PgVault<TV> pg)
            throw new InvalidOperationException($"Expected PgVault<{typeof(TV).Name}> from DI but got {vault.GetType().Name}.");
        return pg;
    }
}

internal sealed class PgVaultJoinQuery<T1, T2, T3, T4, T5> : IVaultJoinQuery<T1, T2, T3, T4, T5>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;
    private readonly PgVault<T3> _t3;
    private readonly PgVault<T4> _t4;
    private readonly PgVault<T5> _t5;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    private readonly List<string> _orderBys;
    private readonly int? _skip;
    private readonly int? _take;

    /// <summary>Creates a join query from already-translated parts (used by the fluent operators).</summary>
    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        PgVault<T3> t3,
        PgVault<T4> t4,
        PgVault<T5> t5,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults,
        List<string> orderBys,
        int? skip,
        int? take)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _t4 = t4;
        _t5 = t5;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
        _orderBys = orderBys;
        _skip = skip;
        _take = take;
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: false);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: true);

    private IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByInternal(LambdaExpression keySelector, bool desc)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { keySelector.Parameters[0], _root },
            { keySelector.Parameters[1], _t2 },
            { keySelector.Parameters[2], _t3 },
            { keySelector.Parameters[3], _t4 },
            { keySelector.Parameters[4], _t5 }
        };

        var colSql = PgJoinQuerySql.ExtractOrderBySql(keySelector, map);
        var order = new List<string>(_orderBys) { $"{colSql} {(desc ? "DESC" : "ASC")}" };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, _t5,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            order, _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Skip(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, _t5,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            Math.Max(0, count),
            _take);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Take(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, _t5,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            _skip,
            Math.Max(0, count));

    /// <inheritdoc/>
    public async Task<List<T1>> ToListAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T1>> selector = (a, b, c, d, e) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T1>> selector = (a, b, c, d, e) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, take: 1);

        var res = await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T1>> selector = (a, b, c, d, e) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 }
        };

        var baseSql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return await PgJoinQuerySql.ExecCountAsync(_root, baseSql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Join<T6>(
        Expression<Func<T5, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
    {
        var t6 = ResolvePgVault<T6>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_t5, t6, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T6), t6 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, t6,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFromLeft<T6>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
    {
        var t6 = ResolvePgVault<T6>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(_root, t6, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T6), t6 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, t6,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFrom<TFrom, T6>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T6 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t6 = ResolvePgVault<T6>();

        var joins = new List<string>(_joins)
        {
            PgJoinExpressionTranslator.BuildJoinDynamic(from, t6, leftKey, rightKey, joinType)
        };

        var vaults = new Dictionary<Type, object>(_vaults)
        {
            { typeof(T6), t6 }
        };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, t6,
            joins,
            new List<string>(_wheres),
            vaults,
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Where(Expression<Func<T1, T2, T3, T4, T5, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 },
            { predicate.Parameters[2], _t3 },
            { predicate.Parameters[3], _t4 },
            { predicate.Parameters[4], _t5 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(
            _root, _t2, _t3, _t4, _t5,
            new List<string>(_joins),
            wheres,
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql).ConfigureAwait(false)).ToList();
    }

    private object GetVault<TV>() where TV : class, IVaultModel
    {
        if (!_vaults.TryGetValue(typeof(TV), out var v))
            throw new InvalidOperationException($"JoinFrom<{typeof(TV).Name},...> requires {typeof(TV).Name} to be part of the current join chain.");
        return v;
    }

    private static PgVault<TV> ResolvePgVault<TV>() where TV : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<TV>>();
        if (vault is not PgVault<TV> pg)
            throw new InvalidOperationException($"Expected PgVault<{typeof(TV).Name}> from DI but got {vault.GetType().Name}.");
        return pg;
    }
}

internal sealed class PgVaultJoinQuery<T1, T2, T3, T4, T5, T6> : IVaultJoinQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
    where T6 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;
    private readonly PgVault<T3> _t3;
    private readonly PgVault<T4> _t4;
    private readonly PgVault<T5> _t5;
    private readonly PgVault<T6> _t6;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    private readonly List<string> _orderBys;
    private readonly int? _skip;
    private readonly int? _take;

    /// <summary>Creates a join query from already-translated parts (used by the fluent operators).</summary>
    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        PgVault<T3> t3,
        PgVault<T4> t4,
        PgVault<T5> t5,
        PgVault<T6> t6,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults,
        List<string> orderBys,
        int? skip,
        int? take)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _t4 = t4;
        _t5 = t5;
        _t6 = t6;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
        _orderBys = orderBys;
        _skip = skip;
        _take = take;
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: false);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector)
        => OrderByInternal(keySelector, desc: true);

    private IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByInternal(LambdaExpression keySelector, bool desc)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { keySelector.Parameters[0], _root },
            { keySelector.Parameters[1], _t2 },
            { keySelector.Parameters[2], _t3 },
            { keySelector.Parameters[3], _t4 },
            { keySelector.Parameters[4], _t5 },
            { keySelector.Parameters[5], _t6 }
        };

        var colSql = PgJoinQuerySql.ExtractOrderBySql(keySelector, map);
        var order = new List<string>(_orderBys) { $"{colSql} {(desc ? "DESC" : "ASC")}" };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, _t6,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            order, _skip, _take);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Skip(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, _t6,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            Math.Max(0, count),
            _take);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Take(int count)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, _t6,
            new List<string>(_joins),
            new List<string>(_wheres),
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            _skip,
            Math.Max(0, count));

    /// <inheritdoc/>
    public async Task<List<T1>> ToListAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T6, T1>> selector = (a, b, c, d, e, f) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 },
            { selector.Parameters[5], _t6 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false)).ToList();
    }

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T6, T1>> selector = (a, b, c, d, e, f) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 },
            { selector.Parameters[5], _t6 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, take: 1);

        var res = await _root.DatabaseProvider.QueryAsync<T1>(sql).ConfigureAwait(false);
        return res.FirstOrDefault();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync()
    {
        Expression<Func<T1, T2, T3, T4, T5, T6, T1>> selector = (a, b, c, d, e, f) => a;

        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 },
            { selector.Parameters[5], _t6 }
        };

        var baseSql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return await PgJoinQuerySql.ExecCountAsync(_root, baseSql).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Where(Expression<Func<T1, T2, T3, T4, T5, T6, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 },
            { predicate.Parameters[2], _t3 },
            { predicate.Parameters[3], _t4 },
            { predicate.Parameters[4], _t5 },
            { predicate.Parameters[5], _t6 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };

        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(
            _root, _t2, _t3, _t4, _t5, _t6,
            new List<string>(_joins),
            wheres,
            new Dictionary<Type, object>(_vaults),
            new List<string>(_orderBys),
            _skip,
            _take);
    }

    /// <inheritdoc/>
    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 },
            { selector.Parameters[2], _t3 },
            { selector.Parameters[3], _t4 },
            { selector.Parameters[4], _t5 },
            { selector.Parameters[5], _t6 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        sql = PgJoinQuerySql.ApplyOrderSkipTake(sql, _orderBys, _skip, _take);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql).ConfigureAwait(false)).ToList();
    }
}
