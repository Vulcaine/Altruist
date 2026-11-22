// PgVaultQuery.cs
using System.Linq.Expressions;

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
/// Join queries use the same predicate rules (column-to-value and column-to-column comparisons, C# null semantics)
/// with every table referenced through its own alias (<c>"t0"</c>, <c>"t1"</c>, ...), so a model can be joined to
/// itself. Join keys, order keys and projected members must be plain member accesses; projections may be
/// <c>(a, b) =&gt; a</c>, <c>new { ... }</c> or <c>new Dto { ... }</c> with members, constants and <c>??</c>.
/// Other shapes throw <see cref="NotSupportedException"/>. Values are inlined as escaped literals.
/// </para>
/// <para>
/// Filters, sort keys and paging applied to the single-table query before <c>Join</c> select the root rows that are
/// joined (LINQ semantics). Paging composes like LINQ; filters and sort keys must precede it.
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
///     .SelectAsync((o, w) =&gt; new OrderRow { OrderId = o.StorageId, OwnerName = w.Name }, ct);
/// </code>
/// </example>
[Service(typeof(IVaultQuery))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PgVaultQuery : IVaultQuery
{
    /// <inheritdoc/>
    /// <remarks>Resolves <c>IVault&lt;T&gt;</c> from the current container (<see cref="Dependencies.Inject{T}"/>).</remarks>
    /// <exception cref="InvalidOperationException">The resolved vault is not a <see cref="PgVault{TVaultModel}"/>.</exception>
    public IVaultQuery<T> From<T>() where T : class, IVaultModel
        => new PgVaultQuery<T>(PgJoinVaults.Resolve<T>());
}

/// <summary>Resolves the Postgres vault of a model for the query layer.</summary>
internal static class PgJoinVaults
{
    /// <summary>The registered <c>IVault&lt;T&gt;</c>, which must be a <see cref="PgVault{TVaultModel}"/>.</summary>
    /// <exception cref="InvalidOperationException">The resolved vault is not a <see cref="PgVault{TVaultModel}"/>.</exception>
    public static PgVault<T> Resolve<T>() where T : class, IVaultModel
    {
        var vault = Dependencies.Inject<IVault<T>>();
        return vault as PgVault<T> ?? throw new InvalidOperationException(
            $"Expected DI to resolve '{typeof(IVault<T>).Name}' as '{typeof(PgVault<T>).Name}', but got '{vault.GetType().Name}'.");
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
        => new PgVaultJoinQuery<T, T2>(PgJoinChain.Start(Vault).Join(PgJoinVaults.Resolve<T2>(), 0, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public Task<List<T>> ToListAsync(CancellationToken ct = default) => Vault.ToListAsync(ct);
    /// <inheritdoc/>
    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default) => Vault.FirstOrDefaultAsync(ct);
    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => Vault.CountAsync(ct);

    /// <inheritdoc/>
    public Task SaveAsync(T entity, bool? saveHistory = false, CancellationToken ct = default)
        => Vault.SaveAsync(entity, saveHistory, ct);

    /// <inheritdoc/>
    public Task SaveBatchAsync(IEnumerable<T> entities, bool? saveHistory = false, CancellationToken ct = default)
        => Vault.SaveBatchAsync(entities, saveHistory, ct);
}

/// <summary>
/// Immutable join query over 2 tables; a thin wrapper over a <see cref="PgJoinChain"/>. The 3- to 6-table
/// variants below are the same with one more lambda parameter.
/// </summary>
internal sealed class PgVaultJoinQuery<T1, T2> : IVaultJoinQuery<T1, T2>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
{
    private readonly PgJoinChain _chain;

    /// <summary>Wraps a chain whose tables are <c>T1, T2</c> in order.</summary>
    public PgVaultJoinQuery(PgJoinChain chain) => _chain = chain;

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2>(_chain.OrderBy(keySelector, descending: false));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T1, T2, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2>(_chain.OrderBy(keySelector, descending: true));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Skip(int count) => new PgVaultJoinQuery<T1, T2>(_chain.Skip(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Take(int count) => new PgVaultJoinQuery<T1, T2>(_chain.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
        => new PgVaultJoinQuery<T1, T2>(_chain.Where(predicate));

    /// <inheritdoc/>
    public Task<List<T1>> ToListAsync(CancellationToken ct = default) => _chain.RootRowsAsync<T1>(_chain.State, ct);

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default)
        => (await _chain.RootRowsAsync<T1>(_chain.State.Take(1), ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => _chain.CountAsync(ct);

    /// <inheritdoc/>
    public Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, TResult>> selector, CancellationToken ct = default)
        where TResult : class
        => _chain.SelectAsync<TResult>(selector, ct);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Join<T3>(
        Expression<Func<T2, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.Join(PgJoinVaults.Resolve<T3>(), 1, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> JoinFromLeft<T3>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.Join(PgJoinVaults.Resolve<T3>(), 0, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> JoinFrom<TFrom, T3>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T3 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.Join(PgJoinVaults.Resolve<T3>(), _chain.IndexOf(typeof(TFrom)), leftKey, rightKey, joinType));
}

/// <summary>Immutable join query over 3 tables; see <see cref="PgVaultJoinQuery{T1, T2}"/>.</summary>
internal sealed class PgVaultJoinQuery<T1, T2, T3> : IVaultJoinQuery<T1, T2, T3>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
{
    private readonly PgJoinChain _chain;

    /// <summary>Wraps a chain whose tables are <c>T1, T2, T3</c> in order.</summary>
    public PgVaultJoinQuery(PgJoinChain chain) => _chain = chain;

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.OrderBy(keySelector, descending: false));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.OrderBy(keySelector, descending: true));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Skip(int count) => new PgVaultJoinQuery<T1, T2, T3>(_chain.Skip(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Take(int count) => new PgVaultJoinQuery<T1, T2, T3>(_chain.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate)
        => new PgVaultJoinQuery<T1, T2, T3>(_chain.Where(predicate));

    /// <inheritdoc/>
    public Task<List<T1>> ToListAsync(CancellationToken ct = default) => _chain.RootRowsAsync<T1>(_chain.State, ct);

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default)
        => (await _chain.RootRowsAsync<T1>(_chain.State.Take(1), ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => _chain.CountAsync(ct);

    /// <inheritdoc/>
    public Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> selector, CancellationToken ct = default)
        where TResult : class
        => _chain.SelectAsync<TResult>(selector, ct);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Join<T4>(
        Expression<Func<T3, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Join(PgJoinVaults.Resolve<T4>(), 2, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> JoinFromLeft<T4>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Join(PgJoinVaults.Resolve<T4>(), 0, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> JoinFrom<TFrom, T4>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T4 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Join(PgJoinVaults.Resolve<T4>(), _chain.IndexOf(typeof(TFrom)), leftKey, rightKey, joinType));
}

/// <summary>Immutable join query over 4 tables; see <see cref="PgVaultJoinQuery{T1, T2}"/>.</summary>
internal sealed class PgVaultJoinQuery<T1, T2, T3, T4> : IVaultJoinQuery<T1, T2, T3, T4>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
{
    private readonly PgJoinChain _chain;

    /// <summary>Wraps a chain whose tables are <c>T1, T2, T3, T4</c> in order.</summary>
    public PgVaultJoinQuery(PgJoinChain chain) => _chain = chain;

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.OrderBy(keySelector, descending: false));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.OrderBy(keySelector, descending: true));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Skip(int count) => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Skip(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Take(int count) => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> predicate)
        => new PgVaultJoinQuery<T1, T2, T3, T4>(_chain.Where(predicate));

    /// <inheritdoc/>
    public Task<List<T1>> ToListAsync(CancellationToken ct = default) => _chain.RootRowsAsync<T1>(_chain.State, ct);

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default)
        => (await _chain.RootRowsAsync<T1>(_chain.State.Take(1), ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => _chain.CountAsync(ct);

    /// <inheritdoc/>
    public Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, TResult>> selector, CancellationToken ct = default)
        where TResult : class
        => _chain.SelectAsync<TResult>(selector, ct);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Join<T5>(
        Expression<Func<T4, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Join(PgJoinVaults.Resolve<T5>(), 3, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFromLeft<T5>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Join(PgJoinVaults.Resolve<T5>(), 0, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFrom<TFrom, T5>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T5 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Join(PgJoinVaults.Resolve<T5>(), _chain.IndexOf(typeof(TFrom)), leftKey, rightKey, joinType));
}

/// <summary>Immutable join query over 5 tables; see <see cref="PgVaultJoinQuery{T1, T2}"/>.</summary>
internal sealed class PgVaultJoinQuery<T1, T2, T3, T4, T5> : IVaultJoinQuery<T1, T2, T3, T4, T5>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
{
    private readonly PgJoinChain _chain;

    /// <summary>Wraps a chain whose tables are <c>T1, T2, T3, T4, T5</c> in order.</summary>
    public PgVaultJoinQuery(PgJoinChain chain) => _chain = chain;

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.OrderBy(keySelector, descending: false));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.OrderBy(keySelector, descending: true));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Skip(int count) => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Skip(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Take(int count) => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5> Where(Expression<Func<T1, T2, T3, T4, T5, bool>> predicate)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5>(_chain.Where(predicate));

    /// <inheritdoc/>
    public Task<List<T1>> ToListAsync(CancellationToken ct = default) => _chain.RootRowsAsync<T1>(_chain.State, ct);

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default)
        => (await _chain.RootRowsAsync<T1>(_chain.State.Take(1), ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => _chain.CountAsync(ct);

    /// <inheritdoc/>
    public Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> selector, CancellationToken ct = default)
        where TResult : class
        => _chain.SelectAsync<TResult>(selector, ct);

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Join<T6>(
        Expression<Func<T5, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Join(PgJoinVaults.Resolve<T6>(), 4, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFromLeft<T6>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Join(PgJoinVaults.Resolve<T6>(), 0, leftKey, rightKey, joinType));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFrom<TFrom, T6>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T6 : class, IVaultModel
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Join(PgJoinVaults.Resolve<T6>(), _chain.IndexOf(typeof(TFrom)), leftKey, rightKey, joinType));
}

/// <summary>Immutable join query over 6 tables; see <see cref="PgVaultJoinQuery{T1, T2}"/>.</summary>
internal sealed class PgVaultJoinQuery<T1, T2, T3, T4, T5, T6> : IVaultJoinQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
    where T6 : class, IVaultModel
{
    private readonly PgJoinChain _chain;

    /// <summary>Wraps a chain whose tables are <c>T1, T2, T3, T4, T5, T6</c> in order.</summary>
    public PgVaultJoinQuery(PgJoinChain chain) => _chain = chain;

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.OrderBy(keySelector, descending: false));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.OrderBy(keySelector, descending: true));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Skip(int count) => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Skip(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Take(int count) => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Take(count));

    /// <inheritdoc/>
    public IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Where(Expression<Func<T1, T2, T3, T4, T5, T6, bool>> predicate)
        => new PgVaultJoinQuery<T1, T2, T3, T4, T5, T6>(_chain.Where(predicate));

    /// <inheritdoc/>
    public Task<List<T1>> ToListAsync(CancellationToken ct = default) => _chain.RootRowsAsync<T1>(_chain.State, ct);

    /// <inheritdoc/>
    public async Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default)
        => (await _chain.RootRowsAsync<T1>(_chain.State.Take(1), ct).ConfigureAwait(false)).FirstOrDefault();

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default) => _chain.CountAsync(ct);

    /// <inheritdoc/>
    public Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, TResult>> selector, CancellationToken ct = default)
        where TResult : class
        => _chain.SelectAsync<TResult>(selector, ct);
}
