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
    /// Handle for working with a prefab manifest + fast child loading.
    /// </summary>
    public interface IPrefabHandle<out TPrefab> where TPrefab : VaultModel
    {
        TPrefab Manifest { get; }

        /// <summary>Flat list of children (as stored on the manifest).</summary>
        IReadOnlyList<PrefabChildRef> Children { get; }

        /// <summary>Load a specific child row using the child's Keyspace + Type + StorageId.</summary>
        Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // PrefabHandle — flat children, no tree, no nodes
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

        public PrefabHandle(VaultModel manifest,
                            List<PrefabChildRef> edges,
                            ICacheProvider cache,
                            IRepositoryFactory repos,
                            PrefabManagerBase manager)
        {
            Manifest = manifest;
            Children = edges ?? [];
            _cache = cache;
            _repos = repos;
            _manager = manager;
        }

        public async Task<TModel?> LoadChildAsync<TModel>(PrefabChildRef child) where TModel : class, IVaultModel
        {
            if (string.IsNullOrWhiteSpace(child.StorageId)) return null;

            // memo by StorageId (sufficient because StorageId is unique within its keyspace)
            if (_loaded.TryGetValue(child.StorageId, out var memo) && memo is TModel typedMemo)
                return typedMemo;

            // resolve CLR type via registry (fast dictionary)
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
            var row = await vault.Where((Func<dynamic, bool>)(x => x.StorageId == child.StorageId)).FirstOrDefaultAsync();
            if (row is null) return null;

            var typed = (IVaultModel)row;
            await _cache.SaveAsync(CacheKey_Model(child.StorageId), typed, child.Keyspace);
            _loaded[child.StorageId] = typed;
            _manager.TrackLoaded(((IVaultModel)Manifest).StorageId, typed);
            return typed as TModel;
        }

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
            // Resolve manifest keyspace from registry
            var manifestKs = VaultRegistry.GetKeyspace(typeof(TPrefab));
            var repo = _repos.Make(manifestKs);

            // Load manifest
            var manifest = await repo.Select<TPrefab>()
                                     .Where(p => p.StorageId == prefabSysId)
                                     .FirstOrDefaultAsync();
            if (manifest is null)
                throw new InvalidOperationException($"Prefab {prefabSysId} not found in keyspace '{manifestKs}'.");

            var edges = getEdges(manifest) ?? new List<PrefabChildRef>();

            // Optionally cache manifest snapshot per manifest keyspace (structure-only)
            await _cache.SaveAsync(CacheKey_Manifest(prefabSysId), edges, manifestKs);

            var inner = new PrefabHandle(manifest, edges, _cache, _repos, this);
            return new PrefabHandle<TPrefab>(inner);
        }

        public async Task SaveCoreAsync<TPrefab>(TPrefab prefab)
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

            // 2) Optional: tie models to prefab via GroupId
            foreach (var m in toSave)
                m.GroupId = prefab.StorageId;

            // 3) Persist loaded nodes grouped by CLR type; each group uses that type’s keyspace
            foreach (var grp in toSave.GroupBy(m => m.GetType()))
            {
                var ksName = VaultRegistry.GetKeyspace(grp.Key);
                var repo = _repos.Make(ksName);
                var vault = repo.Select(grp.Key);                  // ITypeErasedVault
                await vault.SaveBatchAsync(grp.Cast<object>());
            }

            // 4) Persist the manifest itself in its own keyspace
            prefab.Timestamp = DateTime.UtcNow;
            var manifestKs = VaultRegistry.GetKeyspace(typeof(TPrefab));
            var manifestRepo = _repos.Make(manifestKs);
            await manifestRepo.Select<TPrefab>().SaveAsync(prefab);

            // 5) Invalidate cached manifest and any cached loaded children
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
            map.AddOrUpdate(model.StorageId,
                addValueFactory: _ => new WeakReference<IVaultModel>(model),
                updateValueFactory: (_, __) => new WeakReference<IVaultModel>(model));
        }

        private static string CacheKey_Manifest(string prefabSysId) => $"PF:{prefabSysId}:manifest";
    }
}
