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
    IVaultQuery<T> Where(Expression<Func<T, bool>> predicate);
    IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    IVaultQuery<T> Skip(int count);
    IVaultQuery<T> Take(int count);

    IVaultJoinQuery<T, T2> Join<T2>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<T2, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T2 : class, IVaultModel;

    Task<List<T>> ToListAsync();
    Task<T?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    Task SaveAsync(T entity, bool? saveHistory = false);
    Task SaveBatchAsync(IEnumerable<T> entities, bool? saveHistory = false);
}

// ---------------- 1 join (2 tables) ----------------

public interface IVaultJoinQuery<T1, T2>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
{
    // order by any side
    IVaultJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T2, TKey>> keySelector);

    IVaultJoinQuery<T1, T2> Skip(int count);
    IVaultJoinQuery<T1, T2> Take(int count);

    Task<List<T1>> ToListAsync();
    Task<T1?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    IVaultJoinQuery<T1, T2, T3> Join<T3>(
        Expression<Func<T2, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3> JoinFromLeft<T3>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3> JoinFrom<TFrom, T3>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T3 : class, IVaultModel;

    IVaultJoinQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, TResult>> selector)
        where TResult : class;
}

// ---------------- 2 joins (3 tables) ----------------

public interface IVaultJoinQuery<T1, T2, T3>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
{
    IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T3, TKey>> keySelector);

    IVaultJoinQuery<T1, T2, T3> Skip(int count);
    IVaultJoinQuery<T1, T2, T3> Take(int count);

    Task<List<T1>> ToListAsync();
    Task<T1?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    IVaultJoinQuery<T1, T2, T3, T4> Join<T4>(
        Expression<Func<T3, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4> JoinFromLeft<T4>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4> JoinFrom<TFrom, T4>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T4 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> selector)
        where TResult : class;
}

// ---------------- 3 joins (4 tables) ----------------

public interface IVaultJoinQuery<T1, T2, T3, T4>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
{
    IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T4, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T4, TKey>> keySelector);

    IVaultJoinQuery<T1, T2, T3, T4> Skip(int count);
    IVaultJoinQuery<T1, T2, T3, T4> Take(int count);

    Task<List<T1>> ToListAsync();
    Task<T1?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    IVaultJoinQuery<T1, T2, T3, T4, T5> Join<T5>(
        Expression<Func<T4, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFromLeft<T5>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFrom<TFrom, T5>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T5 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, TResult>> selector)
        where TResult : class;
}

// ---------------- 4 joins (5 tables) ----------------

public interface IVaultJoinQuery<T1, T2, T3, T4, T5>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
{
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T4, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T4, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T5, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T5, TKey>> keySelector);

    IVaultJoinQuery<T1, T2, T3, T4, T5> Skip(int count);
    IVaultJoinQuery<T1, T2, T3, T4, T5> Take(int count);

    Task<List<T1>> ToListAsync();
    Task<T1?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Join<T6>(
        Expression<Func<T5, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFromLeft<T6>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFrom<TFrom, T6>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T6 : class, IVaultModel;

    IVaultJoinQuery<T1, T2, T3, T4, T5> Where(Expression<Func<T1, T2, T3, T4, T5, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> selector)
        where TResult : class;
}

// ---------------- 5 joins (6 tables; MAX) ----------------

public interface IVaultJoinQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
    where T6 : class, IVaultModel
{
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T1, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T2, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T3, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T4, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T4, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T5, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T5, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T6, TKey>> keySelector);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T6, TKey>> keySelector);

    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Skip(int count);
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Take(int count);

    Task<List<T1>> ToListAsync();
    Task<T1?> FirstOrDefaultAsync();
    Task<long> CountAsync();

    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Where(Expression<Func<T1, T2, T3, T4, T5, T6, bool>> predicate);

    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, TResult>> selector)
        where TResult : class;
}
