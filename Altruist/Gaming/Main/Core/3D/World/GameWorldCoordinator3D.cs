using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldCoordinator3D : IGameWorldCoordinator
    {
        void AddWorld(WorldIndex3D index, IPhysxWorld3D physx3D);
        void RemoveWorld(int index);
        IGameWorldManager3D? GetWorld(int index);
        IEnumerable<int> GetAllWorldIndices();

    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IGameWorldCoordinator))]
    [Service(typeof(IGameWorldCoordinator3D))]
    public class GameWorldCoordinator3D : IGameWorldCoordinator, IGameWorldCoordinator3D
    {
        private readonly Dictionary<int, GameWorldManager3D> _worlds = new();
        private readonly IWorldPartitioner3D _partitioner;
        private readonly ICacheProvider _cache;
        private readonly IPrefabManager3D _prefabManager;

        public GameWorldCoordinator3D(
            IWorldPartitioner3D partitioner,
            ICacheProvider cache,
            IPrefabManager3D prefabManager)
        {
            _partitioner = partitioner ?? throw new ArgumentNullException(nameof(partitioner));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _prefabManager = prefabManager ?? throw new ArgumentNullException(nameof(prefabManager));
        }

        /// <summary>Adds a new 3D game world and initializes it.</summary>
        public virtual void AddWorld(WorldIndex3D index, IPhysxWorld3D physx3D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager3D(index, physx3D, _partitioner, _cache, _prefabManager);
            manager.Initialize();
            _worlds[index.Index] = manager;
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
        public virtual IGameWorldManager3D? GetWorld(int index)
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
                    // swallow per-world step exceptions to keep other worlds ticking
                }
            }
        }

        public bool Empty() => _worlds.Count == 0;

        public void AddWorld(IWorldIndex index, IPhysxWorld physx2D)
        {
            if (index is not WorldIndex3D)
                throw new ArgumentException("World index must be of type WorldIndex3D", nameof(index));
            if (physx2D is not IPhysxWorld3D)
                throw new ArgumentException("Physx world must be of type IPhysxWorld3D", nameof(physx2D));
            AddWorld((WorldIndex3D)index, (IPhysxWorld3D)physx2D);
        }
    }
}
