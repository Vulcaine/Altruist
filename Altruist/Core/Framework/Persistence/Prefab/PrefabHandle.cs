namespace Altruist.Persistence;

public interface IPrefabHandle<T>
    where T : class, IVaultModel
{
    string? Id { get; }
    void Apply(T entity);
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

        // Keep JSONB in sync
        _owner.ComponentRefs[_meta.Name] = _id;

        // Keep explicit ref property (if exists) in sync too
        _meta.SetRefId(_owner, _id);

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
        _meta.SetRefId(_owner, _id);
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
