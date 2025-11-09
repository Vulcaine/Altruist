/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

#nullable enable

namespace Altruist
{
    // ───────────────────────────────────────────────────────────────────────────
    // Model-type registry (shared)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IModelTypeRegistry
    {
        Type ResolveClrType(string modelType);
        string ResolveModelType(Type clrType);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Structure metadata (shared)
    // ───────────────────────────────────────────────────────────────────────────
    public readonly record struct ModelRef(string StorageId, string Type, string Path)
    {
        public static ModelRef Of(IVaultModel m, string path) => new(m.StorageId, m.Type, path);
    }

    public readonly record struct PrefabRootRef(string StorageId, string Type, string Path)
    {
        public static PrefabRootRef Empty => new("", "", "Root");
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Builder contracts (shared; concrete builders live in 2D/3D files)
    // ───────────────────────────────────────────────────────────────────────────
    public interface IPrefabBuilder
    {
        INodeBuilder Root { get; }
        INodeBuilder AddChild<TModel>(TModel model, string? name = null)
            where TModel : class, IVaultModel;
    }

    public interface INodeBuilder
    {
        string Path { get; }          // e.g., "Root/Player"
        IVaultModel Model { get; }
        INodeBuilder AddChild<TModel>(TModel model, string? name = null)
            where TModel : class, IVaultModel;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Runtime: views and handle (lazy model loads + working-set tracking) — shared
    // ───────────────────────────────────────────────────────────────────────────
    public interface IModelView
    {
        string StorageId { get; }
        string ModelType { get; }
        string Path { get; }
        IModelView? Parent { get; }

        Task<TModel> AsAsync<TModel>() where TModel : class, IVaultModel;

        IModelView? GetChild<TModel>(string childSysId) where TModel : class, IVaultModel;
        IReadOnlyList<IModelView> GetChildren<TModel>() where TModel : class, IVaultModel;
    }

    public interface IPrefabHandle<out TPrefab> where TPrefab : VaultModel
    {
        TPrefab Manifest { get; }
        IModelView Root { get; }

        IModelView? GetChild<TModel>(string childSysId) where TModel : class, IVaultModel;
        IReadOnlyList<IModelView> GetChildren<TModel>() where TModel : class, IVaultModel;
    }

    internal sealed class PrefabHandle : IPrefabHandle<VaultModel>
    {
        private readonly ICacheProvider _cache;
        private readonly IVaultRepository<IKeyspace> _repo;
        internal IModelTypeRegistry Types { get; }
        private readonly IKeyspace _keyspace;
        private readonly PrefabManagerBase _manager;

        public VaultModel Manifest { get; }
        public IModelView Root { get; }

        internal Dictionary<string, List<ModelRef>> Edges { get; }

        // memo
        private readonly Dictionary<string, IVaultModel> _loaded = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IModelView> _views = new(StringComparer.Ordinal);

        public PrefabHandle(VaultModel manifest,
                            Dictionary<string, List<ModelRef>> edges,
                            ICacheProvider cache,
                            IVaultRepository<IKeyspace> repo,
                            IModelTypeRegistry types,
                            IKeyspace keyspace,
                            PrefabManagerBase manager,
                            PrefabRootRef root)
        {
            Manifest = manifest;
            Edges = edges;
            _cache = cache;
            _repo = repo;
            Types = types;
            _keyspace = keyspace;
            _manager = manager;

            Root = MakeView(new ModelRef(root.StorageId, root.Type, "Root"), null, _keyspace);
        }

        public IModelView? GetChild<TModel>(string childSysId) where TModel : class, IVaultModel
            => Root.GetChild<TModel>(childSysId);

        public IReadOnlyList<IModelView> GetChildren<TModel>() where TModel : class, IVaultModel
            => Root.GetChildren<TModel>();

        internal IModelView MakeView(ModelRef m, IModelView? parent, IKeyspace keyspace)
        {
            if (_views.TryGetValue(m.StorageId, out var v)) return v;
            v = new ModelView(this, keyspace, m.StorageId, m.Type, m.Path, parent);
            _views[m.StorageId] = v;
            return v;
        }

        internal async Task<IVaultModel> LoadModelAsync(string sysId, string modelType, IKeyspace keyspace)
        {
            if (_loaded.TryGetValue(sysId, out var m)) return m;

            // 1) cache
            var cached = await _cache.GetAsync<IVaultModel>(CacheKey_Model(sysId), keyspace.Name);
            if (cached is not null) { _loaded[sysId] = cached; _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, cached); return cached; }

            // 2) vault (typed)
            var clr = Types.ResolveClrType(modelType);
            dynamic vault = _repo.Select(clr);
            var row = await vault.Where((Func<dynamic, bool>)(x => x.StorageId == sysId)).FirstOrDefaultAsync();
            if (row == null) throw new InvalidOperationException($"Model {sysId} ({modelType}) not found.");

            await _cache.SaveAsync(CacheKey_Model(sysId), (IVaultModel)row, keyspace.Name);
            _loaded[sysId] = row;
            _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, (IVaultModel)row);
            return row;
        }

        private static string CacheKey_Model(string sysId) => $"VM:{sysId}";
    }

    internal sealed class PrefabHandle<TPrefab> : IPrefabHandle<TPrefab>
        where TPrefab : VaultModel
    {
        private readonly PrefabHandle _inner;
        public PrefabHandle(PrefabHandle inner) => _inner = inner;

        public TPrefab Manifest => (TPrefab)_inner.Manifest;
        public IModelView Root => _inner.Root;

        public IModelView? GetChild<TModel>(string childSysId) where TModel : class, IVaultModel
            => _inner.GetChild<TModel>(childSysId);

        public IReadOnlyList<IModelView> GetChildren<TModel>() where TModel : class, IVaultModel
            => _inner.GetChildren<TModel>();
    }

    internal sealed class ModelView : IModelView
    {
        private readonly PrefabHandle _handle;
        private readonly IKeyspace _keyspace;
        private IVaultModel? _memo;

        public string StorageId { get; }
        public string ModelType { get; }
        public string Path { get; }
        public IModelView? Parent { get; }

        public ModelView(PrefabHandle handle,
                         IKeyspace keyspace,
                         string sysId, string modelType, string path, IModelView? parent)
        {
            _handle = handle;
            _keyspace = keyspace;
            StorageId = sysId;
            ModelType = modelType;
            Path = path;
            Parent = parent;
        }

        public async Task<TModel> AsAsync<TModel>() where TModel : class, IVaultModel
        {
            if (_memo is TModel t) return t;
            var model = await _handle.LoadModelAsync(StorageId, ModelType, _keyspace);
            _memo = model;
            return (TModel)model;
        }

        public IModelView? GetChild<TModel>(string childSysId) where TModel : class, IVaultModel
        {
            if (!_handle.Edges.TryGetValue(StorageId, out var list)) return null;
            var tkey = _handle.Types.ResolveModelType(typeof(TModel));
            var hit = list.FirstOrDefault(m => m.StorageId == childSysId && m.Type == tkey);
            if (hit.StorageId is null or "") return null;
            return _handle.MakeView(hit, this, _keyspace);
        }

        public IReadOnlyList<IModelView> GetChildren<TModel>() where TModel : class, IVaultModel
        {
            if (!_handle.Edges.TryGetValue(StorageId, out var list)) return Array.Empty<IModelView>();
            var tkey = _handle.Types.ResolveModelType(typeof(TModel));
            return list.Where(m => m.Type == tkey)
                       .Select(m => _handle.MakeView(m, this, _keyspace))
                       .ToList();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Prefab Manager Base — shared logic (dimension-specific subclasses call this)
    // ───────────────────────────────────────────────────────────────────────────
    public abstract class PrefabManagerBase
    {
        protected readonly ICacheProvider _cache;
        protected readonly IVaultRepository<IKeyspace> _repo;
        protected readonly IModelTypeRegistry _types;

        // Tracks which models have actually been loaded for a given prefab instance
        // prefabId -> (modelSysId -> WeakReference<IVaultModel>)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WeakReference<IVaultModel>>> _loadedByPrefab
            = new(StringComparer.Ordinal);

        protected PrefabManagerBase(ICacheProvider cache, IVaultRepository<IKeyspace> repo, IModelTypeRegistry types)
        {
            _cache = cache;
            _repo = repo;
            _types = types;
        }

        protected async Task<IPrefabHandle<TPrefab>> LoadCoreAsync<TPrefab>(string prefabSysId, IKeyspace keyspace,
                                                                            Func<TPrefab, PrefabRootRef> getRoot,
                                                                            Func<TPrefab, Dictionary<string, List<ModelRef>>> getEdges)
            where TPrefab : VaultModel
        {
            // Load manifest only (structure)
            var manifest = await _repo.Select<TPrefab>()
                                      .Where(p => p.StorageId == prefabSysId)
                                      .FirstOrDefaultAsync();
            if (manifest is null)
                throw new InvalidOperationException($"Prefab {prefabSysId} not found.");

            var root = getRoot(manifest);
            var edges = getEdges(manifest);

            // Cache manifest snapshot if desired (optional)
            await _cache.SaveAsync(CacheKey_Manifest(prefabSysId),
                                   new ManifestSnapshot(root, edges),
                                   keyspace.Name);

            var inner = new PrefabHandle(manifest, edges, _cache, _repo, _types, keyspace, this, root);
            return new PrefabHandle<TPrefab>(inner);
        }

        public async Task SaveCoreAsync<TPrefab>(TPrefab prefab, IKeyspace keyspace)
            where TPrefab : VaultModel
        {
            // 1) Collect currently loaded models for this prefab (weak set)
            var toSave = new List<IVaultModel>();
            if (_loadedByPrefab.TryGetValue(prefab.StorageId, out var map))
            {
                foreach (var kv in map)
                {
                    if (kv.Value.TryGetTarget(out var model))
                        toSave.Add(model);
                }
            }

            // 2) Ensure grouping (optional): tie models to prefab via GroupId
            foreach (var m in toSave)
                m.GroupId = prefab.StorageId;

            // 3) Batch save by CLR type using repository
            foreach (var grp in toSave.GroupBy(m => m.GetType()))
            {
                var type = grp.Key;
                var vault = _repo.Select(type);                  // ITypeErasedVault
                await vault.SaveBatchAsync(grp.Cast<object>());  // persists only accessed models
            }

            // 4) Persist the manifest itself (structure)
            if (prefab is IVaultModel vm)
                vm.Timestamp = DateTime.UtcNow;

            await _repo.Select<TPrefab>().SaveAsync(prefab);

            // 5) Invalidate caches:
            await _cache.RemoveAndForgetAsync<object>(CacheKey_Manifest(prefab.StorageId), keyspace.Name);
            foreach (var m in toSave)
                await _cache.RemoveAndForgetAsync<IVaultModel>(CacheKey_Model(m.StorageId), keyspace.Name);
        }

        internal void TrackLoaded(string prefabSysId, IVaultModel model)
        {
            var map = _loadedByPrefab.GetOrAdd(prefabSysId, _ => new(StringComparer.Ordinal));
            map.AddOrUpdate(model.StorageId,
                addValueFactory: _ => new WeakReference<IVaultModel>(model),
                updateValueFactory: (_, __) => new WeakReference<IVaultModel>(model));
        }

        private static string CacheKey_Manifest(string prefabSysId) => $"PF:{prefabSysId}:manifest";
        private static string CacheKey_Model(string sysId) => $"VM:{sysId}";

        private sealed record ManifestSnapshot(PrefabRootRef Root, Dictionary<string, List<ModelRef>> Edges);
    }
}
