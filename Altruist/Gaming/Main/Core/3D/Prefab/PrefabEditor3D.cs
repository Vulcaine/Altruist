/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming.ThreeD
{
    public interface IPrefabEditor3D
    {
        PrefabEditor3D Edit(IPrefab3D prefab);
    }

    public sealed class PrefabEditor3D
    {
        private readonly IPrefab3D _prefab;
        private readonly IRepositoryFactory _repos;
        private readonly IServiceProvider _sp;

        internal PrefabEditor3D(IPrefab3D prefab, IRepositoryFactory repos, IServiceProvider sp)
        {
            _prefab = prefab;
            _repos = repos;
            _sp = sp;
        }

        public IPrefab3D Prefab => _prefab;

        /// <summary>Add an already constructed VaultModel instance</summary>
        public TModel Add<TModel>(TModel model) where TModel : class, IVaultModel
        {
            var typeKey = VaultRegistry.GetTypeKey(model.GetType());
            var keyspace = VaultRegistry.GetKeyspace(model.GetType());

            _prefab.Edges.Add(new PrefabChildRef(model.StorageId ?? "", keyspace, typeKey));
            return model;
        }

        /// <summary>
        /// Construct a VaultModel using DI and add to prefab
        /// </summary>
        public TModel Add<TModel>() where TModel : class, IVaultModel
        {
            var model = ActivatorUtilities.CreateInstance<TModel>(_sp);
            return Add(model);
        }

        public TModel? Resolve<TModel>(string storageId)
            where TModel : class, IVaultModel
        {
            if (string.IsNullOrWhiteSpace(storageId))
                return default;

            var repo = _repos.Make(VaultRegistry.GetKeyspace(typeof(TModel)));
            return repo.Select<TModel>()
                       .Where(m => m.StorageId == storageId)
                       .FirstOrDefaultAsync()
                       .GetAwaiter()
                       .GetResult();
        }
    }

    [Service(typeof(IPrefabEditor3D))]
    public sealed class PrefabEditor3DService : IPrefabEditor3D
    {
        private readonly IRepositoryFactory _repos;
        private readonly IServiceProvider _sp;

        public PrefabEditor3DService(IRepositoryFactory repos, IServiceProvider sp)
        {
            _repos = repos;
            _sp = sp;
        }

        public PrefabEditor3D Edit(IPrefab3D prefab)
            => new PrefabEditor3D(prefab, _repos, _sp);
    }
}
