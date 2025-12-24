
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
    IVaultJoinQuery<T, TOther> Join<TOther>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<TOther, object>> rightKey,
        JoinType joinType = JoinType.Inner
    )
    where TOther : class, IVaultModel;

    // --- Execution ---
    Task<List<T>> ToListAsync();
    Task<T?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    Task SaveAsync(T entity, bool? saveHistory = false);
}

public interface IVaultJoinQuery<TLeft, TRight>
    where TLeft : class, IVaultModel
    where TRight : class, IVaultModel
{
    // --- Continue joining ---
    IVaultJoinQuery<TLeft, TNext> Join<TNext>(
        Expression<Func<TRight, object>> leftKey,
        Expression<Func<TNext, object>> rightKey,
        JoinType joinType = JoinType.Inner
    )
    where TNext : class, IVaultModel;

    // --- Filtering ---
    IVaultJoinQuery<TLeft, TRight> Where(
        Expression<Func<TLeft, TRight, bool>> predicate
    );

    // --- Projection ---
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<TLeft, TRight, TResult>> selector)
        where TResult : class;
}