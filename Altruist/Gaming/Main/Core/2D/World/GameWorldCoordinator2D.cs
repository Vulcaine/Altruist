using Altruist.Physx;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    public interface IGameWorldCoordinator2D : IGameWorldCoordinator
    {
        IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D);
    }

    [Service(typeof(IGameWorldCoordinator2D))]
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    public class GameWorldCoordinator2D : IGameWorldCoordinator2D
    {
        private readonly Dictionary<int, IGameWorldManager2D> _worlds = new();
        private readonly IWorldPartitioner2D _partitioner;
        private readonly ICacheProvider _cache;

        private readonly IPrefabManager2D _prefabManager;

        private readonly IPhysxWorldEngineFactory2D _physxWorldEngineFactory;

        public GameWorldCoordinator2D(IWorldPartitioner2D partitioner, ICacheProvider cache, IPrefabManager2D prefabManager,
        IPhysxWorldEngineFactory2D physxWorldEngineFactory, IEnumerable<IWorldIndex2D> gameWorlds)
        {
            _partitioner = partitioner;
            _cache = cache;
            _prefabManager = prefabManager;
            _physxWorldEngineFactory = physxWorldEngineFactory;
            _worlds = gameWorlds
                .Select(index2d =>
                {
                    return AddWorld(index2d, _physxWorldEngineFactory.Create(index2d.Gravity, index2d.FixedDeltaTime));
                })
                .ToDictionary(x => x.Index.Index);
        }

        public IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorldEngine2D engine) => AddWorld(index, new PhysxWorld2D(engine));

        /// <summary>
        /// Adds a new game world and initializes it.
        /// </summary>
        public virtual IGameWorldManager2D AddWorld(WorldIndex2D index, IPhysxWorld2D physx2D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager2D(index, physx2D, _partitioner, _cache, _prefabManager);
            manager.Initialize();
            _worlds[index.Index] = manager;
            return manager;
        }

        /// <summary>
        /// Removes the specified world by index.
        /// </summary>
        public virtual void RemoveWorld(int index)
        {
            _worlds.Remove(index);
        }

        /// <summary>
        /// Gets the GameWorldManager for a given world index.
        /// </summary>
        public virtual IGameWorldManager? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        /// <summary>
        /// Lists all currently loaded world indices.
        /// </summary>
        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        public void Step(float deltaTime)
        {
            foreach (var world in _worlds.Values)
            {
                try
                {
                    world.PhysxWorld.Step(deltaTime);
                }
                catch
                {

                }
            }
        }

        public bool Empty() => _worlds.Count == 0;

        public IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D)
        {
            return AddWorld((WorldIndex2D)index, physx2D);
        }
    }

}
