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

    public IVaultJoinQuery<T, TOther> Join<TOther>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<TOther, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TOther : class, IVaultModel
    {
        var other = Dependencies.Inject<IVault<TOther>>();

        if (other is not PgVault<TOther> otherVault)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TOther>).Name}' as '{typeof(PgVault<TOther>).Name}', " +
                $"but got '{other.GetType().Name}'.");
        }

        return new PgVaultJoinQuery<T, TOther>(
            Vault,
            otherVault,
            leftKey,
            rightKey,
            joinType);
    }

    public Task<List<T>> ToListAsync() => Vault.ToListAsync();
    public Task<T?> FirstOrDefaultAsync() => Vault.FirstOrDefaultAsync();
    public Task<long> CountAsync() => Vault.CountAsync();

    public Task SaveAsync(T entity, bool? saveHistory = false)
        => Vault.SaveAsync(entity, saveHistory);
}

internal sealed class PgVaultJoinQuery<TLeft, TRight>
    : IVaultJoinQuery<TLeft, TRight>
    where TLeft : class, IVaultModel
    where TRight : class, IVaultModel
{
    private readonly PgVault<TLeft> _root;
    private readonly PgVault<TRight> _right;

    private readonly List<string> _joins = new();
    private readonly List<string> _wheres = new();

    public PgVaultJoinQuery(
        PgVault<TLeft> root,
        PgVault<TRight> right,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType)
    {
        _root = root;
        _right = right;

        _joins.Add(PgJoinExpressionTranslator.BuildJoin(
            root,
            right,
            leftKey,
            rightKey,
            joinType));
    }

    public IVaultJoinQuery<TLeft, TNext> Join<TNext>(
        Expression<Func<TRight, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TNext : class, IVaultModel
    {
        var next = Dependencies.Inject<IVault<TNext>>();

        if (next is not PgVault<TNext> nextVault)
        {
            throw new InvalidOperationException(
                $"Expected DI to resolve '{typeof(IVault<TNext>).Name}' as '{typeof(PgVault<TNext>).Name}', " +
                $"but got '{next.GetType().Name}'.");
        }

        var chained = new PgVaultJoinQuery<TLeft, TNext>(
            _root,
            nextVault,
            leftKey,
            rightKey,
            joinType);

        chained._joins.InsertRange(0, _joins);
        chained._wheres.AddRange(_wheres);

        return chained;
    }

    public IVaultJoinQuery<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate)
    {
        _wheres.Add(PgJoinExpressionTranslator.Translate(predicate, _root, _right));
        return this;
    }

    async Task<List<TResult>> IVaultJoinQuery<TLeft, TRight>.SelectAsync<TResult>(
        Expression<Func<TLeft, TRight, TResult>> selector)
    {
        if (!typeof(IVaultModel).IsAssignableFrom(typeof(TResult)))
        {
            throw new NotSupportedException(
                $"Postgres provider can only project to IVaultModel. " +
                $"Type '{typeof(TResult).Name}' is not supported.");
        }

        return (List<TResult>)(object)await SelectVaultAsync((dynamic)selector);
    }

    internal async Task<List<TResult>> SelectVaultAsync<TResult>(
        Expression<Func<TLeft, TRight, TResult>> selector)
        where TResult : class, IVaultModel
    {
        var sql = PgJoinExpressionTranslator.BuildSelect(
            selector,
            _root,
            _right,
            _joins,
            _wheres);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
    }
}
