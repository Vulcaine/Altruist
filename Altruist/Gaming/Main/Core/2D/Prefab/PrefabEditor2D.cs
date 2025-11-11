/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.TwoD
{
    /// <summary>
    /// Minimal editor that appends children into Prefab2D.Edges (flat list).
    /// </summary>
    public sealed class PrefabEditor2D
    {
        private readonly IPrefab2D _prefab;

        public PrefabEditor2D(IPrefab2D prefab)
        {
            _prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        }

        public PrefabEditor2D Add(IVaultModel model)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));

            var typeKey = VaultRegistry.GetTypeKey(model.GetType());
            var keyspace = VaultRegistry.GetKeyspace(model.GetType());

            // StorageId may be empty before first save; it's fine to store empty id initially
            _prefab.Edges.Add(new PrefabChildRef(model.StorageId ?? "", keyspace, typeKey));
            return this;
        }
    }
}
