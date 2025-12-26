
namespace Altruist.Persistence;

/// <summary>
/// In-memory aggregate of interconnected vault models.
/// No prefab table; persistence is delegated to IPrefabs.
/// </summary>
public abstract class PrefabModel : IPrefabModel
{
    /// <summary>
    /// Save all dirty components reachable from this prefab using the current database provider.
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
        => Dependencies.Inject<IPrefabs>().SaveAsync(this, ct);
}

public interface IPrefabModel { }
