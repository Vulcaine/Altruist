// PrefabHandle.cs

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
    private readonly PrefabComponentMetadata _meta;

    private string? _id;
    private bool _loaded;
    private T? _cached;

    public string? Id => _id;

    public PrefabHandle(
        PrefabModel owner,
        PrefabComponentMetadata meta,
        string? id)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _id = id;
    }

    public void Apply(T entity)
    {
        if (entity == null)
            throw new ArgumentNullException(nameof(entity));

        _cached = entity;
        _loaded = true;

        if (string.IsNullOrEmpty(entity.StorageId))
            throw new InvalidOperationException(
                $"Cannot Apply component of type {typeof(T).Name} with empty StorageId. " +
                "Save it first or assign StorageId manually.");

        _id = entity.StorageId;

        _owner.ComponentRefs[_meta.Name] = _id;

        PrefabComponentTracker.Track(_owner, _meta, entity);
        PrefabComponentLifecycle.OnComponentLoadedSync(_owner, _meta, entity);
    }

    internal void ApplyBulk(T? entity)
    {
        _cached = entity;
        _loaded = true;

        if (entity is null)
            return;

        if (string.IsNullOrEmpty(entity.StorageId))
            throw new InvalidOperationException(
                $"Cannot ApplyBulk component of type {typeof(T).Name} with empty StorageId.");

        _id = entity.StorageId;
        _owner.ComponentRefs[_meta.Name] = _id;
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

        var vault = Dependencies.Inject<IVault<T>>();

        var entity = await vault
            .Where(x => x.StorageId == _id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        _cached = entity;
        _loaded = true;

        if (entity is not null)
        {
            PrefabComponentTracker.Track(_owner, _meta, entity);
            await PrefabComponentLifecycle
                .OnComponentLoadedAsync(_owner, _meta, entity, allowAutoLoad: true)
                .ConfigureAwait(false);
        }

        return entity;
    }
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

            // No reflection path would require exposing a non-generic load hook; since
            // this is not on the hot path (bulk load disables autoload), we keep existing behavior.
            //
            // If you want 0 reflection here too, add a non-generic IPrefabHandleBase with LoadObjectAsync().
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
            prefabModel.ComponentRefs.TryGetValue(comp.Name, out var id);

            // IPrefabHandle<TComponent> boxed as object
            var handleObj = comp.HandleFactory(prefabModel, comp, id);

            comp.Setter(prefab, handleObj);
        }
    }
}
