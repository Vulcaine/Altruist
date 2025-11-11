/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Minimal editor that appends children into Prefab3D.Edges (flat list).
    /// </summary>
    public sealed class PrefabEditor3D
    {
        private readonly IPrefab3D _prefab;

        public PrefabEditor3D(IPrefab3D prefab)
        {
            _prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        }

        public PrefabEditor3D Add(IVaultModel model)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));

            var typeKey = VaultRegistry.GetTypeKey(model.GetType());
            var keyspace = VaultRegistry.GetKeyspace(model.GetType());

            _prefab.Edges.Add(new PrefabChildRef(model.StorageId ?? "", keyspace, typeKey));
            return this;
        }
    }
}
