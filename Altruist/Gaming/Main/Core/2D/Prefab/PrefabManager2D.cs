/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.TwoD
{
    public interface IPrefabManager2D
    {
        Task SaveAsync(Prefab2D prefab);

        /// <summary>Load a prefab by storage id and return a handle with a TPrefab-typed manifest.</summary>
        Task<IPrefabHandle<TPrefab>> LoadAsync<TPrefab>(string prefabSysId)
            where TPrefab : Prefab2D;

        /// <summary>
        /// Creates a prefab and optionally configures children before automatic persistence.
        /// </summary>
        Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext2D>? configure = null)
            where TPrefab : Prefab2D;

        IPrefabHandle<Prefab2D> CreateHandle(Prefab2D prefab);
        Task<Bounds2D> ComputeBoundsAsync(IPrefab2D prefab);
    }

    [Service(typeof(IPrefabManager2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    public sealed class PrefabManager2D : PrefabManagerBase, IPrefabManager2D
    {
        private readonly IColliderService2D _colliders;
        private readonly IPrefabFactory2D _factory;

        public PrefabManager2D(
            ICacheProvider cache,
            IColliderService2D colliders,
            IPrefabFactory2D factory)
            : base(cache)
        {
            _colliders = colliders;
            _factory = factory;
        }

        public Task<IPrefabHandle<TPrefab>> LoadAsync<TPrefab>(string prefabSysId)
            where TPrefab : Prefab2D
            => LoadCoreAsync<TPrefab>(prefabSysId, p => p.Edges);

        public Task SaveAsync(Prefab2D prefab)
            => SaveCoreAsync(prefab);

        public async Task<TPrefab> CreateAsync<TPrefab>(Action<PrefabConfigContext2D>? configure = null)
            where TPrefab : Prefab2D
        {
            var prefab = await _factory.CreateAsync<TPrefab>(configure);
            await SaveCoreAsync(prefab);
            return prefab;
        }

        public IPrefabHandle<Prefab2D> CreateHandle(Prefab2D prefab)
        {
            var inner = new PrefabHandle(
                manifest: prefab,
                edges: prefab.Edges,
                cache: _cache,
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
