// JoinQueries.cs
using System.Linq.Expressions;

namespace Altruist.Querying;

/// <summary>
/// SQL join kind used by the <c>Join</c>/<c>JoinFrom</c>/<c>JoinFromLeft</c> methods of
/// <see cref="IVaultQuery{T}"/> and the <c>IVaultJoinQuery</c> family.
/// </summary>
public enum JoinType
{
    /// <summary><c>INNER JOIN</c>: only rows with a match on both sides (default).</summary>
    Inner,
    /// <summary><c>LEFT JOIN</c>: every left row; right-side columns are NULL when there is no match.</summary>
    Left,
    /// <summary><c>RIGHT JOIN</c>: every right row; left-side columns are NULL when there is no match.</summary>
    Right,
    /// <summary><c>FULL JOIN</c>: rows from both sides, matched where possible.</summary>
    Full
}

/// <summary>
/// Entry point for fluent, provider-translated queries over vault models, including multi-table joins
/// (up to 6 tables). Inject it and call <see cref="From{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by the active database provider (Postgres: <c>PgVaultQuery</c>, active when
/// <c>altruist:persistence:database:provider</c> is <c>postgres</c>). Thread-safe: every call returns a new,
/// immutable query object.
/// </para>
/// <para>
/// When to use: reach for <see cref="IVaultQuery"/> when you need <b>joins</b> across vault tables or
/// projections over several tables. For single-table work, injecting <see cref="IVault{TVaultModel}"/> directly
/// is simpler and exposes more (update, delete, <c>AnyAsync</c>, history, cursors).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class LeaderboardService(IVaultQuery query)
/// {
///     public Task&lt;List&lt;ScoreRow&gt;&gt; TopAsync(int n) =&gt;
///         query.From&lt;PlayerVault&gt;()
///              .Join&lt;ScoreVault&gt;(p =&gt; p.StorageId, s =&gt; s.PlayerId)
///              .Where((p, s) =&gt; s.Season == 3)
///              .OrderByDescending((p, s) =&gt; s.Points)
///              .Take(n)
///              .SelectAsync((p, s) =&gt; new ScoreRow { Name = p.Name, Points = s.Points });
/// }
/// </code>
/// </example>
public interface IVaultQuery
{
    /// <summary>Starts a query rooted at the table of vault model <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">A <c>[Vault]</c>-annotated model whose <see cref="IVault{TVaultModel}"/> is registered in DI.</typeparam>
    /// <returns>A new, unfiltered query over <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">The resolved <see cref="IVault{TVaultModel}"/> is not the provider's vault type.</exception>
    IVaultQuery<T> From<T>() where T : class, IVaultModel;
}

/// <summary>
/// Immutable fluent query over a single vault table. Every builder method returns a new query; nothing runs
/// until a terminal method (<see cref="ToListAsync"/>, <see cref="FirstOrDefaultAsync"/>, <see cref="CountAsync"/>).
/// </summary>
/// <remarks>
/// Single-table operations delegate to the model's <see cref="IVault{TVaultModel}"/>, so they behave exactly
/// like the same calls on the vault. Use <see cref="Join{T2}"/> to move to a multi-table
/// <see cref="IVaultJoinQuery{T1, T2}"/>.
/// </remarks>
/// <typeparam name="T">The vault model (table) being queried.</typeparam>
public interface IVaultQuery<T> where T : class, IVaultModel
{
    /// <summary>Adds a filter. Several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Expression translated to SQL by the provider (comparisons, <c>&amp;&amp;</c>, <c>||</c> on mapped properties; captured values are inlined).</param>
    /// <returns>A new query with the filter applied.</returns>
    IVaultQuery<T> Where(Expression<Func<T, bool>> predicate);
    /// <summary>Adds an ascending sort key (appended after any existing sort keys).</summary>
    /// <param name="keySelector">Mapped property to sort by, e.g. <c>x =&gt; x.Timestamp</c>.</param>
    /// <returns>A new query with the sort key appended.</returns>
    IVaultQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector);
    /// <summary>Adds a descending sort key (appended after any existing sort keys).</summary>
    /// <param name="keySelector">Mapped property to sort by.</param>
    /// <returns>A new query with the sort key appended.</returns>
    IVaultQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector);
    /// <summary>Skips <paramref name="count"/> rows of the current window (SQL <c>OFFSET</c>); composes like LINQ (see <see cref="IVault{TVaultModel}.Skip"/>).</summary>
    /// <param name="count">Number of rows to skip.</param>
    /// <returns>A new query with the offset applied.</returns>
    IVaultQuery<T> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (SQL <c>LIMIT</c>); composes like LINQ (see <see cref="IVault{TVaultModel}.Take"/>).</summary>
    /// <param name="count">Maximum number of rows.</param>
    /// <returns>A new query with the limit applied.</returns>
    IVaultQuery<T> Take(int count);

    /// <summary>
    /// Joins another vault table on <c>leftKey = rightKey</c> and switches to a two-table join query.
    /// </summary>
    /// <remarks>
    /// <see cref="Where"/>, ordering, <see cref="Skip"/> and <see cref="Take"/> called on this query <b>before</b>
    /// <c>Join</c> select the root rows that are joined, as in LINQ: <c>From&lt;A&gt;().Where(f).Take(5).Join&lt;B&gt;(...)</c>
    /// joins the first five matching A rows, and the join result keeps their sort keys as its leading sort keys.
    /// </remarks>
    /// <typeparam name="T2">The vault model to join; may be <typeparamref name="T"/> itself (every table gets its own alias).</typeparam>
    /// <param name="leftKey">Property of <typeparamref name="T"/> to join on (a plain property access).</param>
    /// <param name="rightKey">Property of <typeparamref name="T2"/> to join on (a plain property access).</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    /// <returns>A new join query over (<typeparamref name="T"/>, <typeparamref name="T2"/>).</returns>
    IVaultJoinQuery<T, T2> Join<T2>(
        Expression<Func<T, object>> leftKey,
        Expression<Func<T2, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T2 : class, IVaultModel;

    /// <summary>Runs the query and returns all matching rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first row of the current window, or <c>null</c> when none match.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Returns the number of rows the query selects (the paged window when <see cref="Skip"/>/<see cref="Take"/> were called).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts <paramref name="entity"/> into this query's table; same behaviour as
    /// <see cref="IVault{TVaultModel}.SaveAsync"/> (calls <see cref="IVaultModel.OnSave"/>, optimistic concurrency on
    /// <see cref="IVaultModel.Version"/>). Query filters do not affect the save.
    /// </summary>
    /// <param name="entity">The entity to insert or update; its <c>StorageId</c> and <c>Version</c> are updated in place.</param>
    /// <param name="saveHistory">When <c>true</c>, also appends a row to the history table (requires <c>StoreHistory</c> on the <c>[Vault]</c> attribute).</param>
    /// <exception cref="Altruist.Persistence.OptimisticConcurrencyException">The stored version did not match the entity's version.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="saveHistory"/> is <c>true</c> but history is not enabled for the table.</exception>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(T entity, bool? saveHistory = false, CancellationToken ct = default);
    /// <summary>
    /// Upserts several entities atomically; same behaviour as <see cref="IVault{TVaultModel}.SaveBatchAsync"/>.
    /// </summary>
    /// <param name="entities">Entities to insert or update (an empty sequence does nothing).</param>
    /// <param name="saveHistory">When <c>true</c>, also appends history rows (requires <c>StoreHistory</c>).</param>
    /// <exception cref="Altruist.Persistence.OptimisticConcurrencyException">One or more rows had a version mismatch; nothing is written.</exception>
    /// <param name="ct">Cancellation token.</param>
    Task SaveBatchAsync(IEnumerable<T> entities, bool? saveHistory = false, CancellationToken ct = default);
}

// ---------------- 1 join (2 tables) ----------------

/// <summary>
/// Immutable fluent query over two joined vault tables, created by <see cref="IVaultQuery{T}.Join{T2}"/>.
/// Lambdas receive one parameter per table, in join order: <c>(t1, t2) =&gt; ...</c>.
/// </summary>
/// <remarks>
/// <para>
/// The larger join interfaces (<see cref="IVaultJoinQuery{T1, T2, T3}"/> up to 6 tables) have the same
/// members with one more lambda parameter each; the 6-table form cannot join further.
/// </para>
/// <para>
/// Translation rules (Postgres): join keys must be plain property accesses; <c>Where</c> supports
/// <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>&amp;&amp;</c>, <c>||</c> on mapped properties of any table (column to
/// value or column to column), with captured values inlined as SQL literals (no method calls such as
/// <c>Contains</c>); null comparisons follow C# semantics as in <see cref="IVault{TVaultModel}.Where"/>. Every table
/// gets its own alias, so a model can be joined to itself; only <c>JoinFrom</c> needs its <c>TFrom</c> to appear
/// once in the chain.
/// </para>
/// <para>
/// Paging composes like LINQ (see <see cref="IVault{TVaultModel}.Take"/>); <c>Where</c> and <c>OrderBy</c> must
/// come before <c>Skip</c>/<c>Take</c>.
/// </para>
/// <para>
/// <see cref="ToListAsync"/>/<see cref="FirstOrDefaultAsync"/> return the root table's rows only; with
/// one-to-many joins a root row is returned once per match. Use <see cref="SelectAsync{TResult}"/> to read
/// columns from the other tables.
/// </para>
/// </remarks>
/// <typeparam name="T1">The root table (from <see cref="IVaultQuery.From{T}"/>).</typeparam>
/// <typeparam name="T2">The first joined table.</typeparam>
public interface IVaultJoinQuery<T1, T2>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
{
    // order by any side via (t1, t2) => ...
    /// <summary>Appends an ascending sort key taken from any table, e.g. <c>(a, b) =&gt; b.Points</c>.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> keySelector);
    /// <summary>Appends a descending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2> OrderByDescending<TKey>(Expression<Func<T1, T2, TKey>> keySelector);

    /// <summary>Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>; negative values clamp to 0); composes like LINQ.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IVaultJoinQuery<T1, T2> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>; negative values clamp to 0); composes like LINQ.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IVaultJoinQuery<T1, T2> Take(int count);

    /// <summary>Runs the join and returns the matching root (<typeparamref name="T1"/>) rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T1>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first root row of the current window, or <c>null</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Counts the joined rows the query selects (<c>SELECT COUNT(*)</c> over the join, limited to the paged window).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Joins a third table to the <b>most recently joined</b> table (<typeparamref name="T2"/>) on <c>leftKey = rightKey</c>.
    /// To join from the root use <see cref="JoinFromLeft{T3}"/>; from any other table already in the chain use
    /// <see cref="JoinFrom{TFrom, T3}"/>.
    /// </summary>
    /// <param name="leftKey">Property of <typeparamref name="T2"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T3"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3> Join<T3>(
        Expression<Func<T2, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel;

    /// <summary>Joins a third table to the <b>root</b> table (<typeparamref name="T1"/>) on <c>leftKey = rightKey</c>.</summary>
    /// <param name="leftKey">Property of <typeparamref name="T1"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T3"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3> JoinFromLeft<T3>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T3 : class, IVaultModel;

    /// <summary>Joins a third table to any table <typeparamref name="TFrom"/> already in the chain.</summary>
    /// <typeparam name="TFrom">A model already joined in this query.</typeparam>
    /// <typeparam name="T3">The table to join.</typeparam>
    /// <param name="leftKey">Property of <typeparamref name="TFrom"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T3"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TFrom"/> is not part of the current join chain, or appears more than once.</exception>
    IVaultJoinQuery<T1, T2, T3> JoinFrom<TFrom, T3>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T3, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T3 : class, IVaultModel;

    /// <summary>Adds a filter over any of the joined tables; several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Predicate such as <c>(a, b) =&gt; a.Level &gt; 5 &amp;&amp; b.Active == true</c>.</param>
    IVaultJoinQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate);

    /// <summary>Runs the join and projects each row into <typeparamref name="TResult"/>.</summary>
    /// <remarks>
    /// The selector may be a table parameter (<c>(a, b) =&gt; b</c>, all mapped columns of that table) or an object
    /// initializer <c>new TResult { X = a.Prop, Y = b.Prop ?? 0 }</c> whose values are property accesses, constants or
    /// <c>??</c> coalesces, or an anonymous type <c>new { X = a.Prop }</c>. Results are materialized by name: settable
    /// properties of a type with a parameterless constructor, or the constructor parameters of a type without one
    /// (anonymous types, positional records).
    /// </remarks>
    /// <param name="selector">Projection from the joined row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, TResult>> selector, CancellationToken ct = default)
        where TResult : class;
}

// ---------------- 2 joins (3 tables) ----------------

/// <summary>
/// Immutable fluent query over three joined vault tables. Same members and rules as
/// <see cref="IVaultJoinQuery{T1, T2}"/>, with one lambda parameter per table in join order.
/// </summary>
/// <typeparam name="T1">The root table.</typeparam>
/// <typeparam name="T2">The first joined table.</typeparam>
/// <typeparam name="T3">The second joined table.</typeparam>
public interface IVaultJoinQuery<T1, T2, T3>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
{
    /// <summary>Appends an ascending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector);
    /// <summary>Appends a descending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> keySelector);

    /// <summary>Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IVaultJoinQuery<T1, T2, T3> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IVaultJoinQuery<T1, T2, T3> Take(int count);

    /// <summary>Runs the join and returns the matching root (<typeparamref name="T1"/>) rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T1>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first root row of the current window, or <c>null</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Counts the joined rows the query selects (limited to the paged window).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Joins a fourth table to the most recently joined table (<typeparamref name="T3"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T3"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T4"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4> Join<T4>(
        Expression<Func<T3, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel;

    /// <summary>Joins a fourth table to the root table (<typeparamref name="T1"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T1"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T4"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4> JoinFromLeft<T4>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T4 : class, IVaultModel;

    /// <summary>Joins a fourth table to any table <typeparamref name="TFrom"/> already in the chain.</summary>
    /// <typeparam name="TFrom">A model already joined in this query.</typeparam>
    /// <typeparam name="T4">The table to join.</typeparam>
    /// <param name="leftKey">Property of <typeparamref name="TFrom"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T4"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TFrom"/> is not part of the current join chain, or appears more than once.</exception>
    IVaultJoinQuery<T1, T2, T3, T4> JoinFrom<TFrom, T4>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T4, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T4 : class, IVaultModel;

    /// <summary>Adds a filter over any of the joined tables; several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Predicate over the joined row.</param>
    IVaultJoinQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate);

    /// <summary>Runs the join and projects each row into <typeparamref name="TResult"/> (see <see cref="IVaultJoinQuery{T1, T2}.SelectAsync{TResult}"/> for the supported selector shapes).</summary>
    /// <param name="selector">Projection from the joined row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> selector, CancellationToken ct = default)
        where TResult : class;
}

// ---------------- 3 joins (4 tables) ----------------

/// <summary>
/// Immutable fluent query over four joined vault tables. Same members and rules as
/// <see cref="IVaultJoinQuery{T1, T2}"/>, with one lambda parameter per table in join order.
/// </summary>
/// <typeparam name="T1">The root table.</typeparam>
/// <typeparam name="T2">The first joined table.</typeparam>
/// <typeparam name="T3">The second joined table.</typeparam>
/// <typeparam name="T4">The third joined table.</typeparam>
public interface IVaultJoinQuery<T1, T2, T3, T4>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
{
    /// <summary>Appends an ascending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector);
    /// <summary>Appends a descending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, TKey>> keySelector);

    /// <summary>Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IVaultJoinQuery<T1, T2, T3, T4> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IVaultJoinQuery<T1, T2, T3, T4> Take(int count);

    /// <summary>Runs the join and returns the matching root (<typeparamref name="T1"/>) rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T1>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first root row of the current window, or <c>null</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Counts the joined rows the query selects (limited to the paged window).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Joins a fifth table to the most recently joined table (<typeparamref name="T4"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T4"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T5"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> Join<T5>(
        Expression<Func<T4, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel;

    /// <summary>Joins a fifth table to the root table (<typeparamref name="T1"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T1"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T5"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFromLeft<T5>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T5 : class, IVaultModel;

    /// <summary>Joins a fifth table to any table <typeparamref name="TFrom"/> already in the chain.</summary>
    /// <typeparam name="TFrom">A model already joined in this query.</typeparam>
    /// <typeparam name="T5">The table to join.</typeparam>
    /// <param name="leftKey">Property of <typeparamref name="TFrom"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T5"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TFrom"/> is not part of the current join chain, or appears more than once.</exception>
    IVaultJoinQuery<T1, T2, T3, T4, T5> JoinFrom<TFrom, T5>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T5, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T5 : class, IVaultModel;

    /// <summary>Adds a filter over any of the joined tables; several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Predicate over the joined row.</param>
    IVaultJoinQuery<T1, T2, T3, T4> Where(Expression<Func<T1, T2, T3, T4, bool>> predicate);

    /// <summary>Runs the join and projects each row into <typeparamref name="TResult"/> (see <see cref="IVaultJoinQuery{T1, T2}.SelectAsync{TResult}"/> for the supported selector shapes).</summary>
    /// <param name="selector">Projection from the joined row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, TResult>> selector, CancellationToken ct = default)
        where TResult : class;
}

// ---------------- 4 joins (5 tables) ----------------

/// <summary>
/// Immutable fluent query over five joined vault tables. Same members and rules as
/// <see cref="IVaultJoinQuery{T1, T2}"/>, with one lambda parameter per table in join order.
/// </summary>
/// <typeparam name="T1">The root table.</typeparam>
/// <typeparam name="T2">The first joined table.</typeparam>
/// <typeparam name="T3">The second joined table.</typeparam>
/// <typeparam name="T4">The third joined table.</typeparam>
/// <typeparam name="T5">The fourth joined table.</typeparam>
public interface IVaultJoinQuery<T1, T2, T3, T4, T5>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
{
    /// <summary>Appends an ascending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector);
    /// <summary>Appends a descending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, TKey>> keySelector);

    /// <summary>Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> Take(int count);

    /// <summary>Runs the join and returns the matching root (<typeparamref name="T1"/>) rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T1>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first root row of the current window, or <c>null</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Counts the joined rows the query selects (limited to the paged window).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Joins a sixth (final) table to the most recently joined table (<typeparamref name="T5"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T5"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T6"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Join<T6>(
        Expression<Func<T5, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel;

    /// <summary>Joins a sixth (final) table to the root table (<typeparamref name="T1"/>).</summary>
    /// <param name="leftKey">Property of <typeparamref name="T1"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T6"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFromLeft<T6>(
        Expression<Func<T1, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where T6 : class, IVaultModel;

    /// <summary>Joins a sixth (final) table to any table <typeparamref name="TFrom"/> already in the chain.</summary>
    /// <typeparam name="TFrom">A model already joined in this query.</typeparam>
    /// <typeparam name="T6">The table to join.</typeparam>
    /// <param name="leftKey">Property of <typeparamref name="TFrom"/> to join on.</param>
    /// <param name="rightKey">Property of <typeparamref name="T6"/> to join on.</param>
    /// <param name="joinType">SQL join kind; defaults to <see cref="JoinType.Inner"/>.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TFrom"/> is not part of the current join chain, or appears more than once.</exception>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> JoinFrom<TFrom, T6>(
        Expression<Func<TFrom, object>> leftKey,
        Expression<Func<T6, object>> rightKey,
        JoinType joinType = JoinType.Inner)
        where TFrom : class, IVaultModel
        where T6 : class, IVaultModel;

    /// <summary>Adds a filter over any of the joined tables; several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Predicate over the joined row.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5> Where(Expression<Func<T1, T2, T3, T4, T5, bool>> predicate);

    /// <summary>Runs the join and projects each row into <typeparamref name="TResult"/> (see <see cref="IVaultJoinQuery{T1, T2}.SelectAsync{TResult}"/> for the supported selector shapes).</summary>
    /// <param name="selector">Projection from the joined row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, TResult>> selector, CancellationToken ct = default)
        where TResult : class;
}

// ---------------- 5 joins (6 tables; MAX) ----------------

/// <summary>
/// Immutable fluent query over six joined vault tables, the maximum (no further joins). Same members and
/// rules as <see cref="IVaultJoinQuery{T1, T2}"/>, with one lambda parameter per table in join order.
/// </summary>
/// <typeparam name="T1">The root table.</typeparam>
/// <typeparam name="T2">The first joined table.</typeparam>
/// <typeparam name="T3">The second joined table.</typeparam>
/// <typeparam name="T4">The third joined table.</typeparam>
/// <typeparam name="T5">The fourth joined table.</typeparam>
/// <typeparam name="T6">The fifth joined table.</typeparam>
public interface IVaultJoinQuery<T1, T2, T3, T4, T5, T6>
    where T1 : class, IVaultModel
    where T2 : class, IVaultModel
    where T3 : class, IVaultModel
    where T4 : class, IVaultModel
    where T5 : class, IVaultModel
    where T6 : class, IVaultModel
{
    /// <summary>Appends an ascending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderBy<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector);
    /// <summary>Appends a descending sort key taken from any table.</summary>
    /// <param name="keySelector">A property access on one of the joined tables.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> OrderByDescending<TKey>(Expression<Func<T1, T2, T3, T4, T5, T6, TKey>> keySelector);

    /// <summary>Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Skip(int count);
    /// <summary>Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>; negative clamps to 0); composes like LINQ.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Take(int count);

    /// <summary>Runs the join and returns the matching root (<typeparamref name="T1"/>) rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T1>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first root row of the current window, or <c>null</c>.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<T1?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Counts the joined rows the query selects (limited to the paged window).</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Adds a filter over any of the joined tables; several calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Predicate over the joined row.</param>
    IVaultJoinQuery<T1, T2, T3, T4, T5, T6> Where(Expression<Func<T1, T2, T3, T4, T5, T6, bool>> predicate);

    /// <summary>Runs the join and projects each row into <typeparamref name="TResult"/> (see <see cref="IVaultJoinQuery{T1, T2}.SelectAsync{TResult}"/> for the supported selector shapes).</summary>
    /// <param name="selector">Projection from the joined row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TResult>> SelectAsync<TResult>(Expression<Func<T1, T2, T3, T4, T5, T6, TResult>> selector, CancellationToken ct = default)
        where TResult : class;
}
