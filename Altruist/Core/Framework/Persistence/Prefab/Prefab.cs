using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Altruist.UORM;

namespace Altruist.Persistence;

public abstract class PrefabModel : VaultModel, IPrefabModel
{
    [VaultColumn("component-refs")]
    public Dictionary<string, string?> ComponentRefs { get; set; }
        = new(StringComparer.Ordinal);
}

public interface IPrefabVault<TPrefab> : IVault<TPrefab>
    where TPrefab : PrefabModel
{
    TPrefab Construct();
    Task LoadAllComponentsAsync(TPrefab prefab, CancellationToken ct = default);
}

public sealed class PrefabComponentBucket
{
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

public static class PrefabComponentLifecycle
{
    public static void OnComponentLoadedSync(
        PrefabModel owner,
        PrefabComponentMetadata meta,
        IVaultModel component)
    {
        OnComponentLoadedAsync(owner, meta, component, allowAutoLoad: true)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task OnComponentLoadedAsync(
        PrefabModel owner,
        PrefabComponentMetadata meta,
        IVaultModel? component,
        bool allowAutoLoad = true)
    {
        if (meta.OnLoadedCallbacks is { Length: > 0 })
        {
            foreach (var cb in meta.OnLoadedCallbacks)
                await cb(owner, component).ConfigureAwait(false);
        }

        if (!allowAutoLoad)
            return;

        var allComponents = PrefabMetadataRegistry.GetComponents(owner.GetType());
        foreach (var dep in allComponents)
        {
            if (!string.Equals(dep.AutoLoadOn, meta.Name, StringComparison.Ordinal))
                continue;

            var handleObj = dep.Getter(owner);
            if (handleObj is null)
                continue;

            var handleIface = typeof(IPrefabHandle<>).MakeGenericType(dep.ComponentType);
            var loadMethod = handleIface.GetMethod(
                nameof(IPrefabHandle<IVaultModel>.LoadAsync),
                [typeof(CancellationToken)]);

            if (loadMethod is null)
                continue;

            var taskObj = loadMethod.Invoke(handleObj, [CancellationToken.None]);
            if (taskObj is Task task)
                await task.ConfigureAwait(false);
        }
    }
}

public static class PrefabHandleInitializer
{
    public static void InitializeHandles(object prefab, IServiceProvider services)
    {
        if (prefab is not PrefabModel prefabModel)
            return;

        var prefabType = prefab.GetType();
        var components = PrefabMetadataRegistry.GetComponents(prefabType);

        if (components.Count == 0)
            return;

        foreach (var comp in components)
        {
            // 1) Prefer JSONB
            prefabModel.ComponentRefs.TryGetValue(comp.Name, out var id);

            // 2) Fallback to ref binding (explicit property or shadow binding)
            if (string.IsNullOrWhiteSpace(id))
            {
                id = comp.GetRefId(prefabModel);

                // keep jsonb consistent if we found it elsewhere
                if (!string.IsNullOrWhiteSpace(id))
                    prefabModel.ComponentRefs[comp.Name] = id;
            }

            var handleObj = comp.HandleFactory(prefabModel, comp, id);
            comp.Setter(prefab, handleObj);
        }
    }
}
