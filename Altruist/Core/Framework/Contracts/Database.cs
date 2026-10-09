/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Linq.Expressions;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist;

/// <summary>
/// A named database namespace that vault tables live in (a PostgreSQL <i>schema</i>).
/// </summary>
/// <remarks>
/// Vault models pick their keyspace by name through <c>[Vault(..., Keyspace: "...")]</c>. To customise one,
/// write a non-abstract class implementing <see cref="IKeyspace"/> and mark it with <c>[Keyspace("name")]</c>:
/// the Postgres configuration discovers it and registers it as a singleton <see cref="IKeyspace"/>. When no
/// registered keyspace matches a model's name, the provider uses a default keyspace with that name.
/// </remarks>
public interface IKeyspace
{
    /// <summary>Token of the database provider this keyspace belongs to.</summary>
    IDatabaseServiceToken DatabaseToken { get; }
    /// <summary>Keyspace (schema) name, used to qualify table names: <c>"name"."table"</c>.</summary>
    string Name { get; }
}

/// <summary>
/// Implement on a vault model to derive its <see cref="IStoredModel.StorageId"/> yourself instead of getting a random GUID.
/// </summary>
/// <remarks>
/// <see cref="VaultModel.OnSave"/> calls <see cref="GenerateId"/> on <b>every</b> save and overwrites
/// <c>StorageId</c> with the result, so the id must be deterministic from the model's own data (e.g. a hash of a
/// natural key). Returning a fresh random value would insert a new row on each save. Use this for natural-key
/// upserts ("one row per principal"); otherwise leave it off and let the framework assign a GUID once.
/// </remarks>
/// <example>
/// <code>
/// [Vault("user_settings")]
/// public class UserSettings : VaultModel, IIdGenerator
/// {
///     [VaultColumn("user-id")] public string UserId { get; set; } = "";
///     public string GenerateId() =&gt; $"settings:{UserId}";
/// }
/// </code>
/// </example>
public interface IIdGenerator
{
    /// <summary>Returns the stable storage id for the current state of this model.</summary>
    /// <returns>A non-empty id; the same input data must always produce the same id.</returns>
    public string GenerateId();
}

// ITypedModel has moved to Altruist.Protocol (still under namespace Altruist).

/// <summary>
/// A model that is persisted under a string identity (<see cref="StorageId"/>), in a database vault or a cache.
/// </summary>
/// <remarks>
/// For database tables derive from <see cref="VaultModel"/> (which implements <see cref="IVaultModel"/>);
/// implement this interface directly only for stored models that are not SQL vault rows.
/// </remarks>
public interface IStoredModel : ITypedModel
{
    /// <summary>The model's unique id (the <c>id</c> primary-key column for <see cref="VaultModel"/>).</summary>
    public string StorageId { get; set; }

}

/// <summary>
/// Minimal abstract base for stored models: declares <see cref="StorageId"/> and a <see cref="Type"/>
/// discriminator. Prefer <see cref="VaultModel"/> for database tables; it maps both to columns.
/// </summary>
public abstract class StoredModel : IStoredModel
{
    /// <inheritdoc/>
    public abstract string StorageId { get; set; }
    /// <summary>Type discriminator; <see cref="VaultModel"/> sets it to the CLR type name on save.</summary>
    public abstract string Type { get; set; }

    // public virtual string Key { get; set; } = "";

    // public virtual string Group { get; set; } = "";

    // [JsonIgnore]
    // public string StoredId => $"{Group}:{Key}";
}

/// <summary>
/// Factory marker that ties a vault factory to the service token (and so the configuration) it serves.
/// Not used by the built-in providers.
/// </summary>
/// <typeparam name="TToken">The service token type.</typeparam>
/// <typeparam name="TConfig">The configuration type the token refers to.</typeparam>
public interface IVaultFactory<TToken, TConfig> where TConfig : IAltruistConfiguration where TToken : IServiceToken<TConfig>
{
    /// <summary>The token identifying the database service this factory creates vaults for.</summary>
    public TToken Token { get; }
}

/// <summary>
/// A vault model that composes other vault models ("components") by reference; stored as a JSONB map of
/// component name to the component's <see cref="IStoredModel.StorageId"/>.
/// </summary>
public interface IPrefabModel : IVaultModel
{
    // JSONB mapping: component name → StorageId
    /// <summary>Component name to referenced <c>StorageId</c> (<c>null</c> when the component is absent).</summary>
    Dictionary<string, string?> ComponentRefs { get; set; }
}

/// <summary>
/// Contract for a row stored in a database table through <see cref="IVault{TVaultModel}"/>: identity, timestamp,
/// optimistic-concurrency version and a save hook.
/// </summary>
/// <remarks>
/// Derive from <see cref="VaultModel"/> rather than implementing this by hand; it maps the base columns and
/// implements <see cref="OnSave"/>. The model also needs a <c>[Vault("table")]</c> attribute and
/// <c>[VaultColumn]</c> on each persisted property.
/// </remarks>
public interface IVaultModel : IStoredModel
{
    /// <summary>Last save time in UTC (set by <see cref="OnSave"/>).</summary>
    DateTime Timestamp { get; set; }
    /// <summary>
    /// Optimistic-concurrency version. Starts at 1 on insert and is incremented by every successful save; a save
    /// whose version doesn't match the stored row fails with <see cref="Altruist.Persistence.OptimisticConcurrencyException"/>.
    /// Don't set it by hand: always save the instance you loaded.
    /// </summary>
    long Version { get; set; }

    /// <summary>Called by the vault right before each save to assign id, timestamp and type.</summary>
    void OnSave();
}

/// <summary>
/// Base class for database-backed models. Provides the <c>id</c> (primary key), <c>type</c>, <c>version</c> and
/// <c>created-at</c> columns; add your own <c>[VaultColumn]</c> properties and a <c>[Vault("table")]</c> attribute,
/// then inject <see cref="IVault{TVaultModel}"/> to query and save it.
/// </summary>
/// <example>
/// <code>
/// [Vault("player_profile", StoreHistory: true)]
/// public class PlayerProfile : VaultModel
/// {
///     [VaultColumn("display-name")] public string DisplayName { get; set; } = "";
///     [VaultColumn("level")] public int Level { get; set; }
/// }
///
/// // elsewhere: IVault&lt;PlayerProfile&gt; profiles (injected)
/// var p = await profiles.Where(x =&gt; x.DisplayName == name).FirstOrDefaultAsync();
/// </code>
/// </example>
[VaultPrimaryKey(nameof(StorageId))]
public abstract class VaultModel : StoredModel, IVaultModel
{
    /// <summary>
    /// Time of the last save in UTC (column <c>created-at</c>; despite the name it is refreshed on every save by <see cref="OnSave"/>).
    /// </summary>
    [VaultColumn("created-at")]
    public virtual DateTime Timestamp { get; set; } = default!;

    /// <inheritdoc/>
    [VaultColumn("version")]
    public virtual long Version { get; set; } = default!;

    /// <summary>Primary key (column <c>id</c>). Assigned by <see cref="OnSave"/> when empty, or by <see cref="IIdGenerator"/>.</summary>
    [VaultColumn("id")]
    public override string StorageId { get; set; } = default!;

    /// <summary>CLR type name of the saved instance (column <c>type</c>), set by <see cref="OnSave"/>.</summary>
    [VaultColumn("type")]
    public override string Type { get; set; } = default!;

    /// <summary>
    /// Prepares the row for saving: sets <see cref="StorageId"/> (from <see cref="IIdGenerator.GenerateId"/> if
    /// implemented, otherwise keeps the existing id or assigns a new GUID), <see cref="Timestamp"/> = UTC now and
    /// <see cref="Type"/> = the CLR type name. Called by the vault; you rarely call it yourself.
    /// </summary>
    public void OnSave()
    {
        StorageId = this is IIdGenerator idGenerator ? idGenerator.GenerateId() : (string.IsNullOrEmpty(StorageId) ? Guid.NewGuid().ToString() : StorageId);
        Timestamp = DateTime.UtcNow;
        Type = GetType().Name;
    }
}

/// <summary>
/// Base contract for a database connection provider: connection lifecycle (via <see cref="IConnectable"/>),
/// connection string and keyspace (schema) management.
/// </summary>
/// <remarks>
/// Application code normally injects <see cref="IVault{TVaultModel}"/> or <c>IVaultQuery</c> instead. Inject the
/// SQL-specific <see cref="Altruist.Persistence.ISqlDatabaseProvider"/> only for raw SQL.
/// </remarks>
public interface IGeneralDatabaseProvider : IConnectable
{
    /// <summary>Token identifying the database kind (e.g. PostgreSQL).</summary>
    IDatabaseServiceToken Token { get; }
    /// <summary>Returns the full provider connection string built from configuration.</summary>
    string GetConnectionString();
    /// <summary>Creates the keyspace (SQL schema) if it doesn't exist (<c>CREATE SCHEMA IF NOT EXISTS</c>; the name is lower-cased).</summary>
    /// <param name="keyspace">Keyspace / schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CreateKeySpaceAsync(string keyspace, CancellationToken ct = default);
    /// <summary>Switches the provider's default keyspace (Postgres: <c>SET search_path</c>); a no-op for providers that don't support it.</summary>
    /// <remarks>Vaults always use schema-qualified table names, so this does not affect vault queries.</remarks>
    /// <param name="keyspace">Keyspace / schema name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ChangeKeyspaceAsync(string keyspace, CancellationToken ct = default);
}


/// <summary>
/// Entity Framework Core-backed provider contract (LINQ queries over a <see cref="DbContext"/>).
/// No built-in provider implements it; SQL vaults use <see cref="Altruist.Persistence.ISqlDatabaseProvider"/>.
/// </summary>
public interface ILinqDatabaseProvider : IGeneralDatabaseProvider
{
    /// <summary>The underlying EF Core context.</summary>
    DbContext Context { get; }

    /// <summary>Returns all rows of <typeparamref name="TVaultModel"/> matching <paramref name="filter"/>.</summary>
    /// <param name="filter">Row filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(
        Expression<Func<TVaultModel, bool>> filter,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    /// <summary>Runs raw SQL and materializes each row as <paramref name="modelType"/>.</summary>
    /// <param name="modelType">Type to materialize.</param>
    /// <param name="sql">SQL text.</param>
    /// <param name="parameters">Positional parameter values, or <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<object>> QueryAsync(Type modelType, string sql, List<object?>? parameters, CancellationToken ct);

    /// <summary>Returns the single row matching <paramref name="filter"/>, or <c>null</c>.</summary>
    /// <param name="filter">Row filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(
        Expression<Func<TVaultModel, bool>> filter,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    /// <summary>Bulk-updates rows using EF Core <c>SetProperty</c> calls; returns the affected row count.</summary>
    /// <param name="setPropertyCalls">The property assignments.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> UpdateAsync<TVaultModel>(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    /// <summary>Deletes every row matching <paramref name="filter"/>; returns the affected row count.</summary>
    /// <param name="filter">Row filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> DeleteAsync<TVaultModel>(
        Expression<Func<TVaultModel, bool>> filter,
        CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    /// <summary>Deletes the row for <paramref name="model"/>; returns the affected row count.</summary>
    /// <param name="model">The entity to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> DeleteSingleAsync<TVaultModel>(TVaultModel model, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;

    /// <summary>Deletes rows matching <paramref name="model"/>; returns the affected row count.</summary>
    /// <param name="model">The entity whose matching rows are deleted.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> DeleteMultipleAsync<TVaultModel>(TVaultModel model, CancellationToken ct = default)
        where TVaultModel : class, IVaultModel;
}

/// <summary>
/// Repository and immutable fluent query builder for one vault table. This is the main API for reading and
/// writing database models.
/// </summary>
/// <remarks>
/// <para>
/// Inject <c>IVault&lt;TModel&gt;</c> for any <c>[Vault]</c>-annotated <see cref="IVaultModel"/>: the active provider
/// (Postgres when <c>altruist:persistence:database:provider</c> is <c>postgres</c>) registers one singleton per model
/// type. The instance is thread-safe: <see cref="Where"/>, <see cref="OrderBy{TKey}"/>, <see cref="Take"/> and
/// <see cref="Skip"/> return a <b>new</b> vault carrying the added clause and never modify the one you call them on.
/// </para>
/// <para>
/// Need joins across tables? Use <c>Altruist.Querying.IVaultQuery</c>. Need several saves to commit or roll back
/// together? Run them inside the SQL transaction provider (<c>ISqlTransactionProvider.InTransactionAsync</c>).
/// </para>
/// <para>
/// Predicates are translated to SQL: comparisons and <c>&amp;&amp;</c>/<c>||</c> on <c>[VaultColumn]</c> properties,
/// with captured values inlined as literals.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class ProfileService(IVault&lt;PlayerProfile&gt; profiles)
/// {
///     public Task&lt;List&lt;PlayerProfile&gt;&gt; TopAsync() =&gt;
///         profiles.Where(p =&gt; p.Level &gt;= 10).OrderByDescending(p =&gt; p.Level).Take(20).ToListAsync();
///
///     public async Task LevelUpAsync(string id)
///     {
///         var p = await profiles.Where(x =&gt; x.StorageId == id).FirstOrDefaultAsync();
///         if (p is null) return;
///         p.Level++;
///         await profiles.SaveAsync(p);   // versioned upsert
///     }
/// }
/// </code>
/// </example>
/// <typeparam name="TVaultModel">The model (table) this vault reads and writes.</typeparam>
public interface IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    /// <summary>The keyspace (schema) the table lives in.</summary>
    IKeyspace Keyspace { get; }
    /// <summary>
    /// Read access to the table's history (rows appended by saves with <c>saveHistory: true</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">History is not enabled (<c>StoreHistory</c> is off on the <c>[Vault]</c> attribute).</exception>
    IHistoricalVault<TVaultModel> History { get; }

    // Fluent query ops
    /// <summary>Adds a filter; several calls are combined with <c>AND</c>. Returns a new vault.</summary>
    /// <remarks>Filters must come before <see cref="Skip"/>/<see cref="Take"/> (SQL filters before it pages); calling it on a paged chain throws <see cref="InvalidOperationException"/>.</remarks>
    /// <param name="predicate">Filter over mapped properties, e.g. <c>x =&gt; x.Level &gt; 5</c>.</param>
    IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate);
    /// <summary>Appends an ascending sort key (keys apply in call order, like <c>ThenBy</c>). Returns a new vault.</summary>
    /// <remarks>Sort keys must come before <see cref="Skip"/>/<see cref="Take"/>; calling it on a paged chain throws <see cref="InvalidOperationException"/>.</remarks>
    /// <param name="keySelector">Mapped property to sort by.</param>
    IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>Appends a descending sort key. Returns a new vault.</summary>
    /// <remarks>Sort keys must come before <see cref="Skip"/>/<see cref="Take"/>; calling it on a paged chain throws <see cref="InvalidOperationException"/>.</remarks>
    /// <param name="keySelector">Mapped property to sort by.</param>
    IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>
    /// Keeps at most <paramref name="count"/> rows of the current window (<c>LIMIT</c>). Composes like LINQ:
    /// <c>Take(10).Take(3)</c> keeps 3 rows, <c>Take(10).Skip(4)</c> keeps rows 4..9, and
    /// <see cref="FirstOrDefaultAsync"/> after <c>Take(0)</c> finds nothing. Negative counts as 0.
    /// </summary>
    /// <param name="count">Maximum number of rows.</param>
    IVault<TVaultModel> Take(int count);
    /// <summary>
    /// Skips <paramref name="count"/> rows of the current window (<c>OFFSET</c>); combine with
    /// <see cref="OrderBy{TKey}"/> for stable paging. Composes like LINQ: <c>Skip(2).Skip(3)</c> skips 5. Negative counts as 0.
    /// </summary>
    /// <param name="count">Number of rows to skip.</param>
    IVault<TVaultModel> Skip(int count);

    // Terminal query ops
    /// <summary>Runs the query and returns all matching rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TVaultModel>> ToListAsync(CancellationToken ct = default);
    /// <summary>Returns the first matching row (respecting ordering), or <c>null</c> when none match.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<TVaultModel?> FirstOrDefaultAsync(CancellationToken ct = default);
    /// <summary>Returns the first matching row; throws when none match. Prefer <see cref="FirstOrDefaultAsync"/> unless absence is a bug.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No row matched.</exception>
    Task<TVaultModel?> FirstAsync(CancellationToken ct = default);
    /// <summary>Shorthand for <c>Where(predicate).ToListAsync(ct)</c>.</summary>
    /// <param name="predicate">Additional filter, ANDed with existing ones.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate, CancellationToken ct = default);
    /// <summary>Returns the number of rows the query selects (<c>SELECT COUNT(*)</c>): the rows matching the filters, limited to the <c>Skip</c>/<c>Take</c> window when the chain is paged.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs the query (filters, order and paging) selecting only the columns used by <paramref name="selector"/>,
    /// materialized as <typeparamref name="TResult"/> (a vault model with the remaining properties left at their
    /// defaults). Each assigned member reads the column of the source property it is assigned from.
    /// For cross-table projections use <c>IVaultJoinQuery.SelectAsync</c>.
    /// </summary>
    /// <param name="selector">Projection, e.g. <c>x =&gt; new PlayerProfile { StorageId = x.StorageId, Level = x.Level }</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector,
        CancellationToken ct = default)
        where TResult : class, IVaultModel;

    /// <summary>Returns <c>true</c> if any row matches the current filters AND <paramref name="predicate"/> (like <see cref="Where"/>, not allowed on a paged chain).</summary>
    /// <param name="predicate">Additional filter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate, CancellationToken ct = default);

    // Update / Delete
    /// <summary>
    /// Updates every row matching the current filters. Loads the rows, applies the <c>SetProperty</c> assignments in
    /// memory and re-saves them with <see cref="SaveBatchAsync"/> (so <see cref="IVaultModel.OnSave"/> runs and versions
    /// bump); it is not a single SQL <c>UPDATE</c>. Returns the number of rows updated.
    /// </summary>
    /// <param name="setPropertyCalls">Assignments, e.g. <c>s =&gt; s.SetProperty(x =&gt; x.Level, 1)</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Altruist.Persistence.OptimisticConcurrencyException">A row changed between load and save.</exception>
    Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the rows the query selects (the filters, plus the <see cref="Skip"/>/<see cref="Take"/> window in
    /// <see cref="OrderBy{TKey}"/> order when the chain is paged) and returns <c>true</c> if any row was deleted.
    /// A chain without any <see cref="Where"/> or paging throws instead of emptying the table; use
    /// <see cref="DeleteAllAsync"/> for that.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The chain has neither a filter nor paging.</exception>
    Task<bool> DeleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes every row of the table, ignoring the chain's filters and paging, and returns the number of deleted rows.
    /// The explicit way to empty a table; <see cref="DeleteAsync"/> refuses to run without a filter.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<long> DeleteAllAsync(CancellationToken ct = default);

    // Cursor
    /// <summary>
    /// Opens a cursor that reads the query's rows in batches (SQL vaults: 500 rows per <c>LIMIT</c>/<c>OFFSET</c>
    /// query, nothing held open between batches; an unpaged chain is ordered by its sort keys plus the primary key so
    /// batches don't overlap). Use it to stream a large result instead of <see cref="ToListAsync(CancellationToken)"/>; read
    /// it with <c>while (cursor.HasNext) await cursor.NextBatch()</c>.
    /// </summary>
    /// <param name="ct">Cancellation token, observed by every batch.</param>
    Task<ICursor<TVaultModel>> ToCursorAsync(CancellationToken ct = default);

    // Save
    /// <summary>
    /// Inserts or updates <paramref name="entity"/> (upsert on the primary key, or on the first <c>[VaultUniqueKey]</c>
    /// when the model declares one). Calls <see cref="IVaultModel.OnSave"/> first, then writes the returned
    /// <c>StorageId</c> and incremented <c>Version</c> back into the instance. Query filters on this vault are ignored.
    /// </summary>
    /// <param name="entity">The entity to save.</param>
    /// <param name="saveHistory">When <c>true</c>, also appends a snapshot to the <c>&lt;table&gt;_history</c> table (requires <c>StoreHistory</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entity"/> is <c>null</c>.</exception>
    /// <exception cref="Altruist.Persistence.OptimisticConcurrencyException">The stored row's version differs from the entity's (someone saved it in between).</exception>
    /// <exception cref="InvalidOperationException"><paramref name="saveHistory"/> is <c>true</c> but history is not enabled.</exception>
    Task SaveAsync(TVaultModel entity, bool? saveHistory = false, CancellationToken ct = default);
    /// <summary>
    /// Upserts several entities in one atomic statement (all or nothing); same rules as <see cref="SaveAsync"/>.
    /// Prefer it over looping <see cref="SaveAsync"/> when saving many rows.
    /// </summary>
    /// <param name="entities">Entities to save; must not be empty.</param>
    /// <param name="saveHistory">When <c>true</c>, also appends history snapshots (requires <c>StoreHistory</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException"><paramref name="entities"/> is empty.</exception>
    /// <exception cref="Altruist.Persistence.OptimisticConcurrencyException">One or more rows had a version mismatch; nothing is written.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="saveHistory"/> is <c>true</c> but history is not enabled.</exception>
    Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false, CancellationToken ct = default);
}

/// <summary>
/// Read-only, immutable fluent query over a vault's history table (<c>&lt;table&gt;_history</c>), which holds a
/// snapshot per save made with <c>saveHistory: true</c>. Obtain it from <see cref="IVault{TVaultModel}.History"/>.
/// </summary>
/// <typeparam name="TVaultModel">The vault model whose history is queried.</typeparam>
public interface IHistoricalVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    // Fluent filters – same spirit as IVault, but scoped to history
    /// <summary>Adds a filter; several calls are combined with <c>AND</c>. Returns a new history query.</summary>
    /// <param name="predicate">Filter over mapped properties.</param>
    IHistoricalVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate);
    /// <summary>Appends an ascending sort key.</summary>
    /// <param name="keySelector">Mapped property to sort by.</param>
    IHistoricalVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>Appends a descending sort key.</summary>
    /// <param name="keySelector">Mapped property to sort by.</param>
    IHistoricalVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>Limits the result to <paramref name="count"/> rows.</summary>
    /// <param name="count">Maximum number of rows.</param>
    IHistoricalVault<TVaultModel> Take(int count);
    /// <summary>Skips the first <paramref name="count"/> rows.</summary>
    /// <param name="count">Number of rows to skip.</param>
    IHistoricalVault<TVaultModel> Skip(int count);

    /// <summary>Returns history snapshots whose history <c>timestamp</c> lies in [<paramref name="startTime"/>, <paramref name="endTime"/>] (inclusive), plus any filters.</summary>
    /// <param name="startTime">Range start (UTC).</param>
    /// <param name="endTime">Range end (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<TVaultModel>> ToListAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default);
}
