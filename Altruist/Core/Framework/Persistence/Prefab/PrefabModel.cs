// PrefabModel.cs

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

    /// <summary>
    /// Save a single component (by prefab component property name), e.g. nameof(CharacterPrefab.BagInstances).
    /// Should not fall back to loading/hydrating; only persists what is already on the prefab.
    /// </summary>
    public Task SaveComponentAsync(string componentName, CancellationToken ct = default)
        => Dependencies.Inject<IPrefabs>().SaveComponentAsync(this, componentName, ct);
}

public interface IPrefabModel { }
