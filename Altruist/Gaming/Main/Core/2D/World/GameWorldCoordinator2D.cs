namespace Altruist.Gaming.TwoD
{
    [Service(typeof(GameWorldCoordinator2D))]
    public class GameWorldCoordinator2D
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
        public virtual void AddWorld(WorldIndex2D index)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager2D(index, _partitioner, _cache);
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
        public virtual GameWorldManager2D? GetWorld(int index)
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
    }

}