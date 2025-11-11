namespace Altruist.Gaming.TwoD
{
    public sealed class PrefabConfigContext2D
    {
        private readonly PrefabEditor2D _editor;

        internal PrefabConfigContext2D(PrefabEditor2D editor) => _editor = editor;

        public TModel Add<TModel>(TModel model) where TModel : class, IVaultModel
            => _editor.Add(model);

        public TModel? Get<TModel>() where TModel : class, IVaultModel
            => _editor.Prefab.Edges
                .Select(e => _editor.Resolve<TModel>(e.StorageId))
                .FirstOrDefault();
    }
}
