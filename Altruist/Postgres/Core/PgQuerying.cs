// PgVaultQuery.cs

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

    public PgVaultQuery(PgVault<T> vault)
    {
        Vault = vault;
    }

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

    public IVaultJoinQuery<T, TRight> Join<TRight>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<TRight, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TRight : class, IVaultModel
    {
        var rightVault = Dependencies.Inject<IVault<TRight>>();

        if (rightVault is not PgVault<TRight> pgRight)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TRight>).Name}' as '{typeof(PgVault<TRight>).Name}', " +
                $"but got '{rightVault.GetType().Name}'.");
        }

        return new PgVaultJoinQuery<T, TRight>(
            root: Vault,
            right: pgRight,
            leftKey: leftKey,
            rightKey: rightKey,
            joinType: joinType,
            joins: null,
            wheres: null);
    }

    public Task<List<T>> ToListAsync() => Vault.ToListAsync();
    public Task<T?> FirstOrDefaultAsync() => Vault.FirstOrDefaultAsync();
    public Task<long> CountAsync() => Vault.CountAsync();

    public Task SaveAsync(T entity, bool? saveHistory = false)
        => Vault.SaveAsync(entity, saveHistory);
}

internal sealed class PgVaultJoinQuery<TLeft, TCurrent>
    : IVaultJoinQuery<TLeft, TCurrent>
    where TLeft : class, IVaultModel
    where TCurrent : class, IVaultModel
{
    private readonly PgVault<TLeft> _root;
    private readonly PgVault<TCurrent> _current;

    private readonly List<string> _joins;
    private readonly List<string> _wheres;

    public PgVaultJoinQuery(
        PgVault<TLeft> root,
        PgVault<TCurrent> right,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType,
        List<string>? joins,
        List<string>? wheres)
    {
        _root = root;
        _current = right;

        _joins = joins is null ? new List<string>() : new List<string>(joins);
        _wheres = wheres is null ? new List<string>() : new List<string>(wheres);

        _joins.Add(PgJoinExpressionTranslator.BuildJoinDynamic(
            left: root,
            right: right,
            leftKey: leftKey,
            rightKey: rightKey,
            joinType: joinType));
    }

    private PgVaultJoinQuery(
        PgVault<TLeft> root,
        PgVault<TCurrent> current,
        List<string> joins,
        List<string> wheres)
    {
        _root = root;
        _current = current;
        _joins = joins;
        _wheres = wheres;
    }

    // Default: join from CURRENT (last joined)
    public IVaultJoinQuery<TLeft, TNext> Join<TNext>(
        Expression<Func<TCurrent, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TNext : class, IVaultModel
    {
        var nextVault = Dependencies.Inject<IVault<TNext>>();

        if (nextVault is not PgVault<TNext> pgNext)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TNext>).Name}' as '{typeof(PgVault<TNext>).Name}', " +
                $"but got '{nextVault.GetType().Name}'.");
        }

        var joins = new List<string>(_joins);
        joins.Add(PgJoinExpressionTranslator.BuildJoinDynamic(
            left: _current,
            right: pgNext,
            leftKey: leftKey,
            rightKey: rightKey,
            joinType: joinType));

        return new PgVaultJoinQuery<TLeft, TNext>(
            root: _root,
            current: pgNext,
            joins: joins,
            wheres: new List<string>(_wheres));
    }

    // Escape hatch: join from ROOT (left)
    public IVaultJoinQuery<TLeft, TNext> JoinFromLeft<TNext>(
        Expression<Func<TLeft, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TNext : class, IVaultModel
    {
        var nextVault = Dependencies.Inject<IVault<TNext>>();

        if (nextVault is not PgVault<TNext> pgNext)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TNext>).Name}' as '{typeof(PgVault<TNext>).Name}', " +
                $"but got '{nextVault.GetType().Name}'.");
        }

        var joins = new List<string>(_joins);
        joins.Add(PgJoinExpressionTranslator.BuildJoinDynamic(
            left: _root,
            right: pgNext,
            leftKey: leftKey,
            rightKey: rightKey,
            joinType: joinType));

        return new PgVaultJoinQuery<TLeft, TNext>(
            root: _root,
            current: pgNext,
            joins: joins,
            wheres: new List<string>(_wheres));
    }

    // Escape hatch: join from ANY explicit joined type
    public IVaultJoinQuery<TLeft, TNext> JoinFrom<TFrom, TNext>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where TNext : class, IVaultModel
    {
        var fromVault = Dependencies.Inject<IVault<TFrom>>();
        if (fromVault is not PgVault<TFrom> pgFrom)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TFrom>).Name}' as '{typeof(PgVault<TFrom>).Name}', " +
                $"but got '{fromVault.GetType().Name}'.");
        }

        var nextVault = Dependencies.Inject<IVault<TNext>>();
        if (nextVault is not PgVault<TNext> pgNext)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TNext>).Name}' as '{typeof(PgVault<TNext>).Name}', " +
                $"but got '{nextVault.GetType().Name}'.");
        }

        var joins = new List<string>(_joins);
        joins.Add(PgJoinExpressionTranslator.BuildJoinDynamic(
            left: pgFrom,
            right: pgNext,
            leftKey: leftKey,
            rightKey: rightKey,
            joinType: joinType));

        return new PgVaultJoinQuery<TLeft, TNext>(
            root: _root,
            current: pgNext,
            joins: joins,
            wheres: new List<string>(_wheres));
    }

    public IVaultJoinQuery<TLeft, TCurrent> Where(Expression<Func<TLeft, TCurrent, bool>> predicate)
    {
        var nextWheres = new List<string>(_wheres)
        {
            PgJoinExpressionTranslator.Translate(predicate, _root, _current)
        };

        return new PgVaultJoinQuery<TLeft, TCurrent>(
            root: _root,
            current: _current,
            joins: new List<string>(_joins),
            wheres: nextWheres);
    }

    async Task<List<TResult>> IVaultJoinQuery<TLeft, TCurrent>.SelectAsync<TResult>(
        Expression<Func<TLeft, TCurrent, TResult>> selector)
        where TResult : class
    {
        var sql = PgJoinExpressionTranslator.BuildSelect(
            selector, _root, _current, _joins, _wheres);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
    }
}
