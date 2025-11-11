/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Altruist.UORM;

namespace Altruist.Gaming
{
    // ───────────────────────────────────────────────────────────────────────────
    // Minimal shared contracts
    // ───────────────────────────────────────────────────────────────────────────

    public interface IPrefab
    {
        string PrefabId { get; }
        string InstanceId { get; set; }
        string RoomId { get; set; }
    }

    /// <summary>
    /// A flat reference describing a single child row that belongs to a prefab.
    /// This is exactly what is stored in the prefab manifest's Edges array.
    /// </summary>
    public readonly record struct PrefabChildRef(string StorageId, string Keyspace, string Type);

    /// <summary>
    /// Handle for working with a prefab manifest + fast child loading (lazy).
    /// </summary>
    /// <remarks>
    /// TPrefab is covariant, so it must only occur in output positions.
    /// </remarks>
    public interface IPrefabHandle<out TPrefab> where TPrefab : VaultModel
    {
        /// <summary>The loaded prefab manifest, correctly typed.</summary>
        TPrefab Manifest { get; }

        /// <summary>Flat child refs (structure-only, no lazy loads yet).</summary>
        IReadOnlyList<PrefabChildRef> Children { get; }

        /// <summary>Load a specific child row by its reference (keyspace+type+id).</summary>
        Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child)
            where TModel : class, IVaultModel;

        /// <summary>Convenience: load the first child of type TModel, if any.</summary>
        Task<TModel?> GetChildAsync<TModel>()
            where TModel : class, IVaultModel;

        /// <summary>Convenience: load all children of type TModel (in order).</summary>
        Task<IReadOnlyList<TModel>> GetChildrenAsync<TModel>()
            where TModel : class, IVaultModel;

        /// <summary>
        /// “Instantiate” for runtime: currently just returns the already loaded typed manifest.
        /// Kept synchronous so TPrefab remains in a purely covariant position.
        /// </summary>
        TPrefab Instantiate();
    }

    // ───────────────────────────────────────────────────────────────────────────
    // PrefabHandle — flat children, lazy loads on demand
    // ───────────────────────────────────────────────────────────────────────────

    internal sealed class PrefabHandle : IPrefabHandle<VaultModel>
    {
        private readonly ICacheProvider _cache;
        private readonly IRepositoryFactory _repos;
        private readonly PrefabManagerBase _manager;

        public VaultModel Manifest { get; }
        public IReadOnlyList<PrefabChildRef> Children { get; }

        // memo loaded children by StorageId (per manifest)
        private readonly Dictionary<string, IVaultModel> _loaded = new(StringComparer.Ordinal);

        public PrefabHandle(
            VaultModel manifest,
            List<PrefabChildRef> edges,
            ICacheProvider cache,
            IRepositoryFactory repos,
            PrefabManagerBase manager)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Children = edges ?? new List<PrefabChildRef>();
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _repos = repos ?? throw new ArgumentNullException(nameof(repos));
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        }

        public async Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel
        {
            if (string.IsNullOrWhiteSpace(child.StorageId)) return null;

            if (_loaded.TryGetValue(child.StorageId, out var memo) && memo is TModel typedMemo)
                return typedMemo;

            var clr = VaultRegistry.GetClr(child.Type);

            // cache partitioned by keyspace
            var cached = await _cache.GetAsync<IVaultModel>(CacheKey_Model(child.StorageId), child.Keyspace);
            if (cached is TModel cachedT)
            {
                _loaded[child.StorageId] = cachedT;
                _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, cachedT);
                return cachedT;
            }

            // repo per keyspace
            var repo = _repos.Make(child.Keyspace);
            dynamic vault = repo.Select(clr);
            var row = await vault.Where((Func<dynamic, bool>)(x => x.StorageId == child.StorageId))
                                 .FirstOrDefaultAsync();
            if (row is null) return null;

            var typed = (IVaultModel)row;
            await _cache.SaveAsync(CacheKey_Model(child.StorageId), typed, child.Keyspace);
            _loaded[child.StorageId] = typed;
            _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, typed);
            return typed as TModel;
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

    // ───────────────────────────────────────────────────────────────────────────
    // Prefab Manager Base — resolves manifest keyspace via VaultRegistry
    // ───────────────────────────────────────────────────────────────────────────

    public abstract class PrefabManagerBase
    {
        protected readonly ICacheProvider _cache;
        protected readonly IRepositoryFactory _repos;

        // prefabId -> (modelSysId -> WeakReference<IVaultModel>)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WeakReference<IVaultModel>>> _loadedByPrefab
            = new(StringComparer.Ordinal);

        protected PrefabManagerBase(ICacheProvider cache, IRepositoryFactory repos)
        {
            _cache = cache;
            _repos = repos;
        }

        protected async Task<IPrefabHandle<TPrefab>> LoadCoreAsync<TPrefab>(
            string prefabSysId,
            Func<TPrefab, List<PrefabChildRef>> getEdges)
            where TPrefab : VaultModel
        {
            var manifestKs = VaultRegistry.GetKeyspace(typeof(TPrefab));
            var repo = _repos.Make(manifestKs);

            var manifest = await repo.Select<TPrefab>()
                                     .Where(p => p.StorageId == prefabSysId)
                                     .FirstOrDefaultAsync();
            if (manifest is null)
                throw new InvalidOperationException($"Prefab {prefabSysId} not found in keyspace '{manifestKs}'.");

            var edges = getEdges(manifest) ?? new List<PrefabChildRef>();

            await _cache.SaveAsync(CacheKey_Manifest(prefabSysId), edges, manifestKs);

            var inner = new PrefabHandle(manifest, edges, _cache, _repos, this);
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
                var ksName = VaultRegistry.GetKeyspace(grp.Key);
                var repo = _repos.Make(ksName);
                var vault = repo.Select(grp.Key);
                await vault.SaveBatchAsync(grp.Cast<object>());
            }

            prefab.Timestamp = DateTime.UtcNow;
            var manifestKs = VaultRegistry.GetKeyspace(typeof(TPrefab));
            var manifestRepo = _repos.Make(manifestKs);
            await manifestRepo.Select<TPrefab>().SaveAsync(prefab);

            await _cache.RemoveAndForgetAsync<object>(CacheKey_Manifest(prefab.StorageId), manifestKs);
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
