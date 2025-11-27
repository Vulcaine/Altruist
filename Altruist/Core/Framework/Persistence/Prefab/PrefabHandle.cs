using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Persistence;


public interface IPrefabHandle<T>
    where T : class, IVaultModel
{
    /// <summary>StorageId of the underlying component (vault or prefab row), if known.</summary>
    string? Id { get; }

    /// <summary>Attach a pre-fetched component to this handle and mark it loaded.</summary>
    void Apply(T entity);

    /// <summary>Lazily loads the component from its vault on first call, then caches.</summary>
    ValueTask<T?> LoadAsync(CancellationToken ct = default);
}

internal sealed class PrefabHandle<T> : IPrefabHandle<T>
    where T : class, IVaultModel
{
    private readonly PrefabModel _owner;
    private readonly IServiceProvider _services;
    private readonly PrefabComponentMetadata _meta;

    private string? _id;
    private bool _loaded;
    private T? _cached;

    public string? Id => _id;

    public PrefabHandle(
        PrefabModel owner,
        IServiceProvider services,
        PrefabComponentMetadata meta,
        string? id)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _id = id;
    }

    public void Apply(T entity)
    {
        if (entity == null)
            throw new ArgumentNullException(nameof(entity));

        _cached = entity;
        _loaded = true;

        // ensure StorageId exists
        if (string.IsNullOrEmpty(entity.StorageId))
            throw new InvalidOperationException(
                $"Cannot Apply component of type {typeof(T).Name} with empty StorageId. " +
                "Save it first or assign StorageId manually.");

        _id = entity.StorageId;

        // keep prefab manifest in sync
        _owner.ComponentRefs[_meta.Name] = _id;

        // track for cascade SaveAsync(prefab)
        PrefabComponentTracker.Track(_owner, _meta, entity);
    }

    public async ValueTask<T?> LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
            return _cached;

        if (string.IsNullOrWhiteSpace(_id))
        {
            _loaded = true;
            return _cached = null;
        }

        // IVault<T> will be PgVault<T> or PgPrefabVault<T>, resolved via DI.
        var vault = _services.GetRequiredService<IVault<T>>();

        var entity = await vault
            .Where(x => x.StorageId == _id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        _cached = entity;
        _loaded = true;

        if (entity is not null)
            PrefabComponentTracker.Track(_owner, _meta, entity);

        return entity;
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
            prefabModel.ComponentRefs.TryGetValue(comp.Name, out var id);

            // IPrefabHandle<TComponent> boxed as object
            var handleObj = comp.HandleFactory(prefabModel, services, comp, id);

            comp.Setter(prefab, handleObj);
        }
    }
}
