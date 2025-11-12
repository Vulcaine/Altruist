/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming
{
    public interface IPrefab
    {
        string PrefabId { get; }
        string InstanceId { get; set; }
        string RoomId { get; set; }
    }

    public readonly record struct PrefabChildRef(string StorageId, string Keyspace, string Type);

    public interface IPrefabHandle<out TPrefab> where TPrefab : VaultModel
    {
        TPrefab Manifest { get; }
        IReadOnlyList<PrefabChildRef> Children { get; }
        Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel;
        Task<TModel?> GetChildAsync<TModel>() where TModel : class, IVaultModel;
        Task<IReadOnlyList<TModel>> GetChildrenAsync<TModel>() where TModel : class, IVaultModel;
        TPrefab Instantiate();
    }

    internal sealed class PrefabHandle : IPrefabHandle<VaultModel>
    {
        private readonly ICacheProvider _cache;
        private readonly PrefabManagerBase _manager;

        public VaultModel Manifest { get; }
        public IReadOnlyList<PrefabChildRef> Children { get; }

        private readonly Dictionary<string, IVaultModel> _loaded = new(StringComparer.Ordinal);

        public PrefabHandle(
            VaultModel manifest,
            List<PrefabChildRef> edges,
            ICacheProvider cache,
            PrefabManagerBase manager)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Children = edges ?? new List<PrefabChildRef>();
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public async Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel
        {
            if (string.IsNullOrWhiteSpace(child.StorageId)) return null;

            if (_loaded.TryGetValue(child.StorageId, out var memo) && memo is TModel typedMemo)
                return typedMemo;

            var clr = VaultRegistry.GetClr(child.Type);

            var cached = await _cache.GetAsync<IVaultModel>(CacheKey_Model(child.StorageId), child.Keyspace);
            if (cached is TModel cachedT)
            {
                _loaded[child.StorageId] = cachedT;
                _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, cachedT);
                return cachedT;
            }

            var row = await VaultRegistry.FindByStorageIdAsync(clr, child.StorageId);
            if (row is null) return null;

            await _cache.SaveAsync(CacheKey_Model(child.StorageId), row, child.Keyspace);
            _loaded[child.StorageId] = row;
            _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, row);
            return row as TModel;
        }

        public async Task<TModel?> GetChildAsync<TModel>() where TModel : class, IVaultModel
        {
            var typeKey = VaultRegistry.GetTypeKey(typeof(TModel));
            var first = Children.FirstOrDefault(c => string.Equals(c.Type, typeKey, StringComparison.Ordinal));
            if (string.IsNullOrEmpty(first.StorageId)) return null;
            return await LoadChildAsync<TModel>(first);
        }

        public async Task<IReadOnlyList<TModel>> GetChildrenAsync<TModel>() where TModel : class, IVaultModel
        {
            var typeKey = VaultRegistry.GetTypeKey(typeof(TModel));
            var matches = Children.Where(c => string.Equals(c.Type, typeKey, StringComparison.Ordinal)).ToArray();
            var list = new List<TModel>(matches.Length);
            foreach (var c in matches)
            {
                var m = await LoadChildAsync<TModel>(c);
                if (m != null) list.Add(m);
            }
            return list;
        }

        public VaultModel Instantiate() => Manifest;

        private static string CacheKey_Model(string sysId) => $"VM:{sysId}";
    }

    internal sealed class PrefabHandle<TPrefab> : IPrefabHandle<TPrefab>
        where TPrefab : VaultModel
    {
        private readonly PrefabHandle _inner;
        public PrefabHandle(PrefabHandle inner) => _inner = inner;

        public TPrefab Manifest => (TPrefab)_inner.Manifest;
        public IReadOnlyList<PrefabChildRef> Children => _inner.Children;

        public Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel
            => _inner.LoadChildAsync<TModel>(child);

        public Task<TModel?> GetChildAsync<TModel>() where TModel : class, IVaultModel
            => _inner.GetChildAsync<TModel>();

        public Task<IReadOnlyList<TModel>> GetChildrenAsync<TModel>() where TModel : class, IVaultModel
            => _inner.GetChildrenAsync<TModel>();

        public TPrefab Instantiate() => (TPrefab)_inner.Instantiate();
    }

    public abstract class PrefabManagerBase
    {
        protected readonly ICacheProvider _cache;

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WeakReference<IVaultModel>>> _loadedByPrefab
            = new(StringComparer.Ordinal);

        protected PrefabManagerBase(ICacheProvider cache)
        {
            _cache = cache;
        }

        protected async Task<IPrefabHandle<TPrefab>> LoadCoreAsync<TPrefab>(
            string prefabSysId,
            Func<TPrefab, List<PrefabChildRef>> getEdges)
            where TPrefab : VaultModel
        {
            var vault = VaultRegistry.GetVault<TPrefab>();

            System.Linq.Expressions.Expression<Func<TPrefab, bool>> pred = p => p.StorageId == prefabSysId;
            var manifest = await vault.Where(pred).FirstOrDefaultAsync();
            if (manifest is null)
                throw new InvalidOperationException($"Prefab {prefabSysId} not found.");

            var edges = getEdges(manifest) ?? new List<PrefabChildRef>();

            var ks = VaultRegistry.GetKeyspace(typeof(TPrefab));
            await _cache.SaveAsync(CacheKey_Manifest(prefabSysId), edges, ks);

            var inner = new PrefabHandle(manifest, edges, _cache, this);
            return new PrefabHandle<TPrefab>(inner);
        }

        public async Task SaveCoreAsync<TPrefab>(TPrefab prefab)
            where TPrefab : VaultModel
        {
            var toSave = new List<IVaultModel>();
            if (_loadedByPrefab.TryGetValue(prefab.StorageId, out var map))
            {
                foreach (var kv in map)
                    if (kv.Value.TryGetTarget(out var model))
                        toSave.Add(model);
            }

            foreach (var m in toSave)
                m.GroupId = prefab.StorageId;

            foreach (var grp in toSave.GroupBy(m => m.GetType()))
            {
                var modelClr = grp.Key;
                var vaultObj = VaultRegistry.GetVault(modelClr);

                var cast = typeof(Enumerable).GetMethod("Cast")!.MakeGenericMethod(modelClr);
                var toList = typeof(Enumerable).GetMethod("ToList")!.MakeGenericMethod(modelClr);
                var typedList = toList.Invoke(null, [cast.Invoke(null, [grp])!])!;

                var vaultIface = typeof(IVault<>).MakeGenericType(modelClr);
                var saveBatch = vaultIface.GetMethod("SaveBatchAsync", new[] { typeof(IEnumerable<>).MakeGenericType(modelClr), typeof(bool?) })
                               ?? vaultIface.GetMethod("SaveBatchAsync", new[] { typeof(IEnumerable<>).MakeGenericType(modelClr) });

                object? taskObj;
                if (saveBatch!.GetParameters().Length == 2)
                    taskObj = saveBatch.Invoke(vaultObj, [typedList, null]);
                else
                    taskObj = saveBatch.Invoke(vaultObj, [typedList]);

                await ((Task)taskObj!).ConfigureAwait(false);
            }

            prefab.Timestamp = DateTime.UtcNow;

            var manifestVault = VaultRegistry.GetVault<TPrefab>();
            await manifestVault.SaveAsync(prefab);

            var ksManifest = VaultRegistry.GetKeyspace(typeof(TPrefab));
            await _cache.RemoveAndForgetAsync<object>(CacheKey_Manifest(prefab.StorageId), ksManifest);

            foreach (var m in toSave)
            {
                var ks = VaultRegistry.GetKeyspace(m.GetType());
                await _cache.RemoveAndForgetAsync<IVaultModel>($"VM:{m.StorageId}", ks);
            }
        }

        internal void TrackLoaded(string prefabSysId, IVaultModel model)
        {
            var map = _loadedByPrefab.GetOrAdd(prefabSysId, _ => new(StringComparer.Ordinal));
            map[model.StorageId] = new WeakReference<IVaultModel>(model);
        }

        private static string CacheKey_Manifest(string prefabSysId) => $"PF:{prefabSysId}:manifest";
    }
}
