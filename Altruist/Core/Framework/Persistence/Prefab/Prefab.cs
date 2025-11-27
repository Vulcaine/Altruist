using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Altruist.Persistence;

public abstract class PrefabModel : VaultModel, IPrefabModel
{
    // Stored as JSONB in Postgres
    public Dictionary<string, string?> ComponentRefs { get; set; }
        = new(StringComparer.Ordinal);
}

public interface IPrefabVault<TPrefab> : IVault<TPrefab>
    where TPrefab : PrefabModel
{
}


public sealed class PrefabComponentBucket
{
    // one component per [PrefabComponent] property in practice
    public ConcurrentDictionary<PrefabComponentMetadata, IVaultModel> Components { get; } = new();
}

public static class PrefabComponentTracker
{
    private static readonly ConditionalWeakTable<PrefabModel, PrefabComponentBucket> _byPrefab = new();

    public static void Track(PrefabModel prefab, PrefabComponentMetadata meta, IVaultModel component)
    {
        var bucket = _byPrefab.GetOrCreateValue(prefab);
        bucket.Components[meta] = component;
    }

    public static PrefabComponentBucket? GetBucket(PrefabModel prefab)
        => _byPrefab.TryGetValue(prefab, out var b) ? b : null;

    public static void Clear(PrefabModel prefab)
        => _byPrefab.Remove(prefab);
}
