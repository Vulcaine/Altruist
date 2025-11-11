/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.TwoD
{
    public interface IPrefabManager2D
    {
        Task SaveAsync(Prefab2D prefab);
        Task<IPrefabHandle<Prefab2D>> LoadAsync(string prefabSysId);

        IPrefabHandle<Prefab2D> CreateHandle(Prefab2D prefab);
        Task<Bounds2D> ComputeBoundsAsync(IPrefab2D prefab);
    }

    [Service(typeof(IPrefabManager2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    public sealed class PrefabManager2D : PrefabManagerBase, IPrefabManager2D
    {
        private readonly IColliderService2D _colliders;

        public PrefabManager2D(ICacheProvider cache, IRepositoryFactory repos, IColliderService2D colliders)
            : base(cache, repos)
        {
            _colliders = colliders;
        }

        public Task<IPrefabHandle<Prefab2D>> LoadAsync(string prefabSysId)
            => LoadCoreAsync<Prefab2D>(prefabSysId, p => p.Edges);

        public Task SaveAsync(Prefab2D prefab)
            => SaveCoreAsync(prefab);

        public IPrefabHandle<Prefab2D> CreateHandle(Prefab2D prefab)
        {
            var inner = new PrefabHandle(
                manifest: prefab,
                edges: prefab.Edges,
                cache: _cache,
                repos: _repos,
                manager: this);

            return new PrefabHandle<Prefab2D>(inner);
        }

        public Task<Bounds2D> ComputeBoundsAsync(IPrefab2D prefab)
        {
            var handle = CreateHandle((Prefab2D)prefab);
            return _colliders.ComputeBoundsAsync(handle);
        }
    }
}
