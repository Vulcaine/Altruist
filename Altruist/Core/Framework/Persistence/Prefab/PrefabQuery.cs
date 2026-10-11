using System.Linq.Expressions;

namespace Altruist.Persistence;

/// <summary>
/// Loads and saves prefabs: aggregates of related vault models (one root model plus single and collection
/// components) declared on a <see cref="PrefabModel"/> subclass with
/// <see cref="PrefabComponentRootAttribute"/> and <see cref="PrefabComponentRefAttribute"/>.
/// </summary>
/// <remarks>
/// Registered with <c>[Service(typeof(IPrefabs))]</c> by the database provider (Postgres: <c>PgPrefabs</c>, active when
/// <c>altruist:persistence:database:provider</c> is <c>postgres</c>). Use it when you need a root model together
/// with its related rows in one call; for a single table use <see cref="IVault{TVaultModel}"/> directly.
/// <see cref="PrefabModel.SaveAsync"/> and <see cref="PrefabModel.SaveComponentAsync"/> are shortcuts that resolve this service.
/// </remarks>
/// <example>
/// <code>
/// var player = await prefabs.Query&lt;PlayerPrefab&gt;()
///     .Where(p =&gt; p.Account.Name == "alice")
///     .Include(p =&gt; p.Items)
///     .FirstOrDefaultAsync();
/// player!.Items.Add(newItem);
/// await prefabs.SaveComponentAsync(player, nameof(PlayerPrefab.Items));
/// </code>
/// </example>
public interface IPrefabs
{
    /// <summary>
    /// Save a single component from a prefab by component/property name (PrefabDocument component key).
    /// Example: "BagInstances", "ItemInstances", "Character" etc.
    /// </summary>
    Task SaveComponentAsync(PrefabModel prefab, string componentName, CancellationToken ct = default);

    /// <summary>
    /// Upserts every component currently on the prefab (root, singles and every element of collections) in one
    /// batched statement. Only models that are dirty or whose dirtiness is unknown are written
    /// (<see cref="System.ComponentModel.IChangeTracking"/> or a bool <c>IsDirty</c>/<c>Dirty</c> property is honoured).
    /// </summary>
    /// <remarks>
    /// Upsert only: elements removed from a collection are not deleted from the database; delete them through their vault.
    /// Null components are skipped (not loaded is not the same as empty). Every saved model needs a non-empty StorageId.
    /// </remarks>
    /// <param name="prefab">The prefab to save; its root must be non-null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The root is null, or a model has an empty StorageId.</exception>
    Task SaveAsync(PrefabModel prefab, CancellationToken ct = default);
    /// <summary>Starts a new query that loads prefabs of type <typeparamref name="TPrefab"/> anchored on their root model's table.</summary>
    /// <typeparam name="TPrefab">The prefab type; must have exactly one <see cref="PrefabComponentRootAttribute"/> property.</typeparam>
    /// <returns>A fresh mutable query builder.</returns>
    IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new();
}

/// <summary>
/// Fluent query over prefabs of type <typeparamref name="TPrefab"/>: filters on the root (and collection
/// components via <c>Any</c>), then optionally eager-loads components.
/// </summary>
/// <remarks>
/// The builder is mutable (each call modifies and returns the same instance) and is not thread-safe; create a
/// new one per query via <see cref="IPrefabs.Query{TPrefab}"/>. Components not included stay null after loading.
/// </remarks>
/// <typeparam name="TPrefab">The prefab type.</typeparam>
public interface IPrefabQuery<TPrefab>
    where TPrefab : PrefabModel, new()
{

    /// <summary>
    /// Adds a filter, combined with earlier filters by AND. Supports <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>,
    /// <c>==</c>/<c>!=</c> between a component member and a value (e.g. <c>p =&gt; p.Root.Name == name</c>),
    /// <c>Contains</c> of a component member in a client-side collection (e.g. <c>p =&gt; ids.Contains(p.Root.StorageId)</c>,
    /// one array parameter), and <c>Any</c> on collection components (e.g. <c>p =&gt; p.Items.Any(i =&gt; i.Kind == "x")</c>).
    /// Members of single refs and owned components filter through an EXISTS on their table.
    /// </summary>
    /// <param name="predicate">The filter; other expression shapes throw <see cref="NotSupportedException"/>.</param>
    /// <returns>This query.</returns>
    IPrefabQuery<TPrefab> Where(Expression<Func<TPrefab, bool>> predicate);

    /// <summary>Eager-loads one component property after the roots are fetched.</summary>
    /// <typeparam name="TProp">Type of the component property.</typeparam>
    /// <param name="selector">A direct member access to the component, e.g. <c>p =&gt; p.Items</c>.</param>
    /// <returns>This query.</returns>
    IPrefabQuery<TPrefab> Include<TProp>(Expression<Func<TPrefab, TProp>> selector);

    /// <summary>Eager-loads every component declared on the prefab. Prefer <see cref="Include{TProp}"/> when only some are needed.</summary>
    /// <returns>This query.</returns>
    IPrefabQuery<TPrefab> IncludeAll();

    /// <summary>Runs the query and returns all matching prefabs with the requested components hydrated.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The prefabs; empty when nothing matches.</returns>
    Task<List<TPrefab>> ToListAsync(CancellationToken ct = default);
    /// <summary>Runs the query limited to one root and returns it, or null when nothing matches.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first prefab or null.</returns>
    Task<TPrefab?> FirstOrDefaultAsync(CancellationToken ct = default);
}
