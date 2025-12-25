using System.Linq.Expressions;

namespace Altruist.Persistence;

public interface IPrefabs
{
    IPrefabQuery<TPrefab> Query<TPrefab>()
        where TPrefab : PrefabModel, new();
}

public interface IPrefabQuery<TPrefab>
    where TPrefab : PrefabModel, new()
{
    IPrefabQuery<TPrefab> Where(Expression<Func<TPrefab, bool>> predicate);

    IPrefabQuery<TPrefab> Include<TProp>(Expression<Func<TPrefab, TProp>> selector);

    Task<List<TPrefab>> ToListAsync(CancellationToken ct = default);
    Task<TPrefab?> FirstOrDefaultAsync(CancellationToken ct = default);
}
