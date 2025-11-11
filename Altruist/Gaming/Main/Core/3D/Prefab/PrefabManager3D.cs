/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    public interface IPrefabManager3D
    {
        Task SaveAsync(Prefab3D prefab);

        /// <summary>Load a prefab by storage id and return a handle with a TPrefab-typed manifest.</summary>
        Task<IPrefabHandle<TPrefab>> LoadAsync<TPrefab>(string prefabSysId)
            where TPrefab : Prefab3D;

        /// <summary>
        /// Creates a prefab and optionally configures children before automatic persistence.
        /// </summary>
        Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext3D>? configure = null)
            where TPrefab : Prefab3D;

        IPrefabHandle<Prefab3D> CreateHandle(Prefab3D prefab);
        Task<Bounds3D> ComputeBoundsAsync(IPrefab3D prefab);
    }

    [Service(typeof(IPrefabManager3D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    public sealed class PrefabManager3D : PrefabManagerBase, IPrefabManager3D
    {
        private readonly IColliderService3D _colliders;
        private readonly IPrefabFactory3D _factory;

        public PrefabManager3D(
            ICacheProvider cache,
            IRepositoryFactory repos,
            IColliderService3D colliders,
            IPrefabFactory3D factory)
            : base(cache, repos)
        {
            _colliders = colliders;
            _factory = factory;
        }

        public Task<IPrefabHandle<TPrefab>> LoadAsync<TPrefab>(string prefabSysId)
            where TPrefab : Prefab3D
            => LoadCoreAsync<TPrefab>(prefabSysId, p => p.Edges);

        public Task SaveAsync(Prefab3D prefab)
            => SaveCoreAsync(prefab);

        public async Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext3D>? configure = null)
            where TPrefab : Prefab3D
        {
            var prefab = await _factory.CreateAsync<TPrefab>(configure);
            await SaveCoreAsync(prefab);
            return prefab;
        }

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
