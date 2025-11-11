namespace Altruist.Gaming.ThreeD
{
    public sealed class PrefabConfigContext3D
    {
        private readonly PrefabEditor3D _editor;

        internal PrefabConfigContext3D(PrefabEditor3D editor) => _editor = editor;

        public TModel Add<TModel>(TModel model) where TModel : class, IVaultModel
            => _editor.Add(model);

        public TModel? Get<TModel>() where TModel : class, IVaultModel
            => _editor.Prefab.Edges
                .Select(e => _editor.Resolve<TModel>(e.StorageId))
                .FirstOrDefault();
    }
}
