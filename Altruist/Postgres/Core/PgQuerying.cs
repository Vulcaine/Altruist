using System.Linq.Expressions;

using Altruist.Persistence.Postgres.Querying;
using Altruist.Querying;

namespace Altruist.Persistence.Postgres;

[Service(typeof(IVaultQuery))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgVaultQuery : IVaultQuery
{
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

internal sealed class PgVaultQuery<T> : IVaultQuery<T>
    where T : class, IVaultModel
{
    internal readonly PgVault<T> Vault;

    public PgVaultQuery(PgVault<T> vault) => Vault = vault;

    public IVaultQuery<T> Where(Expression<Func<T, bool>> predicate)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Where(predicate));

    public IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderBy(keySelector));

    public IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderByDescending(keySelector));

    public IVaultQuery<T> Skip(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Skip(count));

    public IVaultQuery<T> Take(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Take(count));

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
            wheres: new List<string>(),
            vaults: new Dictionary<Type, object>
            {
                { typeof(T), Vault },
                { typeof(T2), rightVault }
            });
    }

    public Task<List<T>> ToListAsync() => Vault.ToListAsync();
    public Task<T?> FirstOrDefaultAsync() => Vault.FirstOrDefaultAsync();
    public Task<long> CountAsync() => Vault.CountAsync();

    public Task SaveAsync(T entity, bool? saveHistory = false)
        => Vault.SaveAsync(entity, saveHistory);

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

// ---------------- Join query: 1 join ----------------

internal sealed class PgVaultJoinQuery<T1, T2> : IVaultJoinQuery<T1, T2>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
{
    private readonly PgVault<T1> _root;
    private readonly PgVault<T2> _t2;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;
    private readonly Dictionary<Type, object> _vaults;

    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults)
    {
        _root = root;
        _t2 = t2;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
    }

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

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, new List<string>(_wheres), vaults);
    }

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

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, new List<string>(_wheres), vaults);
    }

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

        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, t3, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };
        return new PgVaultJoinQuery<T1, T2>(_root, _t2, new List<string>(_joins), wheres, new Dictionary<Type, object>(_vaults));
    }

    public async Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, TResult>> selector)
        where TResult : class
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { selector.Parameters[0], _root },
            { selector.Parameters[1], _t2 }
        };

        var sql = PgJoinExpressionTranslator.BuildSelect(selector, _root, _joins, _wheres, map);
        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
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

    public PgVaultJoinQuery(
        PgVault<T1> root,
        PgVault<T2> t2,
        PgVault<T3> t3,
        List<string> joins,
        List<string> wheres,
        Dictionary<Type, object> vaults)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
    }

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

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, new List<string>(_wheres), vaults);
    }

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

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, new List<string>(_wheres), vaults);
    }

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

        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, t4, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate)
    {
        var map = new Dictionary<ParameterExpression, object>
        {
            { predicate.Parameters[0], _root },
            { predicate.Parameters[1], _t2 },
            { predicate.Parameters[2], _t3 }
        };

        var wheres = new List<string>(_wheres) { PgJoinExpressionTranslator.Translate(predicate, map) };
        return new PgVaultJoinQuery<T1, T2, T3>(_root, _t2, _t3, new List<string>(_joins), wheres, new Dictionary<Type, object>(_vaults));
    }

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
        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
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

    public PgVaultJoinQuery(
        PgVault<T1> root, PgVault<T2> t2, PgVault<T3> t3, PgVault<T4> t4,
        List<string> joins, List<string> wheres, Dictionary<Type, object> vaults)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _t4 = t4;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5> Join<T5>(
        Expression<Func<T4, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
    {
        var t5 = ResolvePgVault<T5>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(_t4, t5, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T5), t5 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_root, _t2, _t3, _t4, t5, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFromLeft<T5>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
    {
        var t5 = ResolvePgVault<T5>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(_root, t5, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T5), t5 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_root, _t2, _t3, _t4, t5, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFrom<TFrom, T5>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T5 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t5 = ResolvePgVault<T5>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(from, t5, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T5), t5 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_root, _t2, _t3, _t4, t5, joins, new List<string>(_wheres), vaults);
    }

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
        return new PgVaultJoinQuery<T1, T2, T3, T4>(_root, _t2, _t3, _t4, new List<string>(_joins), wheres, new Dictionary<Type, object>(_vaults));
    }

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
        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
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

// ---------------- Join query: 4 joins ----------------

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

    public PgVaultJoinQuery(
        PgVault<T1> root, PgVault<T2> t2, PgVault<T3> t3, PgVault<T4> t4, PgVault<T5> t5,
        List<string> joins, List<string> wheres, Dictionary<Type, object> vaults)
    {
        _root = root;
        _t2 = t2;
        _t3 = t3;
        _t4 = t4;
        _t5 = t5;
        _joins = joins;
        _wheres = wheres;
        _vaults = vaults;
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Join<T6>(
        Expression<Func<T5, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
    {
        var t6 = ResolvePgVault<T6>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(_t5, t6, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T6), t6 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_root, _t2, _t3, _t4, _t5, t6, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFromLeft<T6>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
    {
        var t6 = ResolvePgVault<T6>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(_root, t6, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T6), t6 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_root, _t2, _t3, _t4, _t5, t6, joins, new List<string>(_wheres), vaults);
    }

    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFrom<TFrom, T6>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T6 : class, IVaultModel
    {
        var from = GetVault<TFrom>();
        var t6 = ResolvePgVault<T6>();
        var joins = new List<string>(_joins) { PgJoinExpressionTranslator.BuildJoinDynamic(from, t6, leftKey, rightKey, joinType) };
        var vaults = new Dictionary<Type, object>(_vaults) { { typeof(T6), t6 } };
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_root, _t2, _t3, _t4, _t5, t6, joins, new List<string>(_wheres), vaults);
    }

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
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_root, _t2, _t3, _t4, _t5, new List<string>(_joins), wheres, new Dictionary<Type, object>(_vaults));
    }

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
        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
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

// ---------------- Join query: 5 joins (MAX) ----------------

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

    public PgVaultJoinQuery(
        PgVault<T1> root, PgVault<T2> t2, PgVault<T3> t3, PgVault<T4> t4, PgVault<T5> t5, PgVault<T6> t6,
        List<string> joins, List<string> wheres, Dictionary<Type, object> vaults)
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
    }

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
        return new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_root, _t2, _t3, _t4, _t5, _t6, new List<string>(_joins), wheres, new Dictionary<Type, object>(_vaults));
    }

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
        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
    }
}
