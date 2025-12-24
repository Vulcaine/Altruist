using System.Linq.Expressions;

using Altruist.Persistence.Postgres.Querying;
using Altruist.Querying;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence.Postgres;

public sealed class PgVaultQuery : IVaultQuery
{
    private readonly IServiceProvider _services;

    public PgVaultQuery(IServiceProvider services)
    {
        _services = services;
    }

    public IVaultQuery<T> From<T>() where T : class, IVaultModel
    {
        var repo = _services.GetRequiredService<IVaultRepository<IKeyspace>>();
        var vault = (PgVault<T>)repo.Select<T>();
        return new PgVaultQuery<T>(vault, _services);
    }
}

internal sealed class PgVaultQuery<T> : IVaultQuery<T>
    where T : class, IVaultModel
{
    internal readonly PgVault<T> Vault;
    private readonly IServiceProvider _services;

    public PgVaultQuery(PgVault<T> vault, IServiceProvider services)
    {
        Vault = vault;
        _services = services;
    }

    public IVaultQuery<T> Where(Expression<Func<T, bool>> predicate)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Where(predicate), _services);

    public IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderBy(keySelector), _services);

    public IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
        => new PgVaultQuery<T>((PgVault<T>)Vault.OrderByDescending(keySelector), _services);

    public IVaultQuery<T> Skip(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Skip(count), _services);

    public IVaultQuery<T> Take(int count)
        => new PgVaultQuery<T>((PgVault<T>)Vault.Take(count), _services);

    public IVaultJoinQuery<T, TOther> Join<TOther>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<TOther, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TOther : class, IVaultModel
    {
        var repo = _services.GetRequiredService<IVaultRepository<IKeyspace>>();
        var otherVault = (PgVault<TOther>)repo.Select<TOther>();

        return new PgVaultJoinQuery<T, TOther>(
            Vault,
            otherVault,
            _services,
            leftKey,
            rightKey,
            joinType
        );
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
    private readonly IServiceProvider _services;

    private readonly List<string> _joins = new();
    private readonly List<string> _wheres = new();

    public PgVaultJoinQuery(
        PgVault<TLeft> root,
        PgVault<TRight> right,
        IServiceProvider services,
        LambdaExpression leftKey,
        LambdaExpression rightKey,
        JoinType joinType)
    {
        _root = root;
        _right = right;
        _services = services;

        _joins.Add(
            PgJoinExpressionTranslator.BuildJoin(
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
        var repo = _services.GetRequiredService<IVaultRepository<IKeyspace>>();
        var nextVault = (PgVault<TNext>)repo.Select<TNext>();

        var next = new PgVaultJoinQuery<TLeft, TNext>(
            _root,
            nextVault,
            _services,
            leftKey,
            rightKey,
            joinType);

        next._joins.InsertRange(0, _joins);
        next._wheres.AddRange(_wheres);

        return next;
    }

    public IVaultJoinQuery<TLeft, TRight> Where(
        Expression<Func<TLeft, TRight, bool>> predicate)
    {
        _wheres.Add(
            PgJoinExpressionTranslator.Translate(predicate, _root, _right));
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

        return (List<TResult>)(object)
            await SelectVaultAsync((dynamic)selector);
    }

    internal async Task<List<TResult>> SelectVaultAsync<TResult>(
    Expression<Func<TLeft, TRight, TResult>> selector)
    where TResult : class, IVaultModel
    {
        var sql = PgJoinExpressionTranslator.BuildSelect(
            selector, _root, _right, _joins, _wheres);

        return (await _root.DatabaseProvider.QueryAsync<TResult>(sql)).ToList();
    }
}
