/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    public interface IPrefabManager3D
    {
        Task SaveAsync(Prefab3D prefab);
        Task<IPrefabHandle<Prefab3D>> LoadAsync(string prefabSysId);

        IPrefabHandle<Prefab3D> CreateHandle(Prefab3D prefab);
        Task<Bounds3D> ComputeBoundsAsync(IPrefab3D prefab);
    }

    [Service(typeof(IPrefabManager3D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    public sealed class PrefabManager3D : PrefabManagerBase, IPrefabManager3D
    {
        private readonly IColliderService3D _colliders;

        public PrefabManager3D(ICacheProvider cache, IRepositoryFactory repos, IColliderService3D colliders)
            : base(cache, repos)
        {
            _colliders = colliders;
        }

        public Task<IPrefabHandle<Prefab3D>> LoadAsync(string prefabSysId)
            => LoadCoreAsync<Prefab3D>(prefabSysId, p => p.Edges);

        public Task SaveAsync(Prefab3D prefab)
            => SaveCoreAsync(prefab);

        public IPrefabHandle<Prefab3D> CreateHandle(Prefab3D prefab)
        {
            var inner = new PrefabHandle(
                manifest: prefab,
                edges: prefab.Edges,
                cache: _cache,
                repos: _repos,
                manager: this);

            return new PrefabHandle<Prefab3D>(inner);
        }

        public Task<Bounds3D> ComputeBoundsAsync(IPrefab3D prefab)
        {
            var handle = CreateHandle((Prefab3D)prefab);
            return _colliders.ComputeBoundsAsync(handle);
        }
    }
}
