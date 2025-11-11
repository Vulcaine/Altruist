/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.UORM;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming.TwoD
{
    public interface IPrefabEditor2D
    {
        PrefabEditor2D Edit(IPrefab2D prefab);
    }

    public sealed class PrefabEditor2D
    {
        private readonly IPrefab2D _prefab;
        private readonly IRepositoryFactory _repos;
        private readonly IServiceProvider _sp;

        internal PrefabEditor2D(IPrefab2D prefab, IRepositoryFactory repos, IServiceProvider sp)
        {
            _prefab = prefab;
            _repos = repos;
            _sp = sp;
        }

        public IPrefab2D Prefab => _prefab;

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
            if (string.IsNullOrWhiteSpace(storageId)) return default;

            var repo = _repos.Make(VaultRegistry.GetKeyspace(typeof(TModel)));
            return repo.Select<TModel>()
                       .Where(m => m.StorageId == storageId)
                       .FirstOrDefaultAsync()
                       .GetAwaiter()
                       .GetResult();
        }
    }

    [Service(typeof(IPrefabEditor2D))]
    public sealed class PrefabEditor2DService : IPrefabEditor2D
    {
        private readonly IRepositoryFactory _repos;
        private readonly IServiceProvider _sp;

        public PrefabEditor2DService(IRepositoryFactory repos, IServiceProvider sp)
        {
            _repos = repos;
            _sp = sp;
        }

        public PrefabEditor2D Edit(IPrefab2D prefab)
            => new PrefabEditor2D(prefab, _repos, _sp);
    }
}
