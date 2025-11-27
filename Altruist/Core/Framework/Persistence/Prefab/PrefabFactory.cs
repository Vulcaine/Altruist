namespace Altruist.Persistence
{
    public interface IPrefabFactory
    {
        TPrefab Construct<TPrefab>()
            where TPrefab : class, IPrefabModel, new();
    }

    [Service(typeof(IPrefabFactory))]
    public class PrefabFactory : IPrefabFactory
    {
        private IServiceProvider _services;

        public PrefabFactory(IServiceProvider services) => _services = services;

        public TPrefab Construct<TPrefab>()
            where TPrefab : class, IPrefabModel, new()
        {
            var prefab = new TPrefab();
            PrefabHandleInitializer.InitializeHandles(prefab, _services);

            return prefab;
        }
    }
}
