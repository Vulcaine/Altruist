using System.Linq.Expressions;

namespace Altruist.Persistence;

public interface IPrefabs
{
    /// <summary>
    /// Save a single component from a prefab by component/property name (PrefabDocument component key).
    /// Example: "BagInstances", "ItemInstances", "Character" etc.
    /// </summary>
    Task SaveComponentAsync(PrefabModel prefab, string componentName, CancellationToken ct = default);

    Task SaveAsync(PrefabModel prefab, CancellationToken ct = default);
    IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new();
}

public interface IPrefabQuery<TPrefab>
    where TPrefab : PrefabModel, new()
{

    IPrefabQuery<TPrefab> Where(Expression<Func<TPrefab, bool>> predicate);

    IPrefabQuery<TPrefab> Include<TProp>(Expression<Func<TPrefab, TProp>> selector);

    IPrefabQuery<TPrefab> IncludeAll();

    Task<List<TPrefab>> ToListAsync(CancellationToken ct = default);
    Task<TPrefab?> FirstOrDefaultAsync(CancellationToken ct = default);
}
