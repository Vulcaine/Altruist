
using System.Linq.Expressions;

namespace Altruist.Querying;

public enum JoinType
{
    Inner,
    Left,
    Right,
    Full
}

public interface IVaultQuery
{
    IVaultQuery<T> From<T>() where T : class, IVaultModel;
}

public interface IVaultQuery<T> where T : class, IVaultModel
{
    // --- IVault equivalence ---
    IVaultQuery<T> Where(Expression<Func<T, bool>> predicate);
    IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    IVaultQuery<T> Skip(int count);
    IVaultQuery<T> Take(int count);

    // --- Join entry ---
    IVaultJoinQuery<T, TRight> Join<TRight>(
         Expression<Func<T, object>> leftKey,
         Expression<Func<TRight, object>> rightKey,
         JoinType joinType = JoinType.Inner)
         where TRight : class, IVaultModel;

    // --- Execution ---
    Task<List<T>> ToListAsync();
    Task<T?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    Task SaveAsync(T entity, bool? saveHistory = false);
}

public interface IVaultJoinQuery<TLeft, TCurrent>
    where TLeft : class, IVaultModel
    where TCurrent : class, IVaultModel
{
    // Default: join from the CURRENT (last-joined) table
    IVaultJoinQuery<TLeft, TNext> Join<TNext>(
        Expression<Func<TCurrent, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TNext : class, IVaultModel;

    // Escape hatch: join from the ROOT (left)
    IVaultJoinQuery<TLeft, TNext> JoinFromLeft<TNext>(
        Expression<Func<TLeft, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TNext : class, IVaultModel;

    // Escape hatch: join from ANY previously joined type (explicit)
    IVaultJoinQuery<TLeft, TNext> JoinFrom<TFrom, TNext>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where TNext : class, IVaultModel;

    IVaultJoinQuery<TLeft, TCurrent> Where(Expression<Func<TLeft, TCurrent, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<TLeft, TCurrent, TResult>> selector)
        where TResult : class;
}