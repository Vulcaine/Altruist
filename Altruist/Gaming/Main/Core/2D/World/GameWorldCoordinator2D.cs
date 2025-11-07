using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    [Service(typeof(IGameWorldCoordinator))]
    public class GameWorldCoordinator2D : IGameWorldCoordinator
    {
        private readonly Dictionary<int, GameWorldManager2D> _worlds = new();
        private readonly IWorldPartitioner2D _partitioner;
        private readonly ICacheProvider _cache;

        public GameWorldCoordinator2D(IWorldPartitioner2D partitioner, ICacheProvider cache)
        {
            _partitioner = partitioner;
            _cache = cache;
        }

        /// <summary>
        /// Adds a new game world and initializes it.
        /// </summary>
        public virtual void AddWorld(WorldIndex2D index, IPhysxWorld2D physx2D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager2D(index, physx2D, _partitioner, _cache);
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

        public void AddWorld(IWorldIndex index, IPhysxWorld physx2D)
        {
            if (index is not WorldIndex2D)
                throw new ArgumentException("World index must be of type WorldIndex2D", nameof(index));
            if (physx2D is not IPhysxWorld2D)
                throw new ArgumentException("Physx world must be of type IPhysxWorld2D", nameof(physx2D));
            AddWorld((WorldIndex2D)index, (IPhysxWorld2D)physx2D);
        }
    }

}