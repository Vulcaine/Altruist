using Altruist.Gaming.World.ThreeD;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldOrganizer3D : IGameWorldOrganizer
    {
        IGameWorldManager3D AddWorld(WorldIndex3D index, IPhysxWorld3D physx3D);
        void RemoveWorld(int index);
        IGameWorldManager3D? GetWorld(int index);
        IEnumerable<int> GetAllWorldIndices();

    }

    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [Service(typeof(IGameWorldOrganizer))]
    [Service(typeof(IGameWorldOrganizer3D))]
    public class GameWorldOrganizer3D : IGameWorldOrganizer3D
    {
        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldPartitioner3D _partitioner;
        private readonly IPrefabManager3D _prefabManager;

        private readonly IPhysxWorldEngineFactory3D _physxWorldEngineFactory;

        public GameWorldOrganizer3D(
            IWorldPartitioner3D partitioner,
            IPrefabManager3D prefabManager,
            IPhysxWorldEngineFactory3D physxWorldEngineFactory,
            IWorldLoader3D worldLoader3D,
            IEnumerable<IWorldIndex3D> gameWorlds)
        {
            _partitioner = partitioner;
            _prefabManager = prefabManager;
            _physxWorldEngineFactory = physxWorldEngineFactory;
            _worlds = gameWorlds
                .Select(index3d =>
                {
                    if (index3d.DataPath != null)
                    {
                        return AddWorld(index3d, worldLoader3D.LoadFromPath(index3d.DataPath, index3d.Gravity, index3d.FixedDeltaTime));
                    }

                    return AddWorld(index3d, _physxWorldEngineFactory.Create(index3d.Gravity, index3d.FixedDeltaTime));
                })
                .ToDictionary(x => x.Index.Index);
        }

        public IGameWorldManager3D AddWorld(IWorldIndex3D index, IPhysxWorldEngine3D engine) => AddWorld(index, new PhysxWorld3D(engine));

        /// <summary>Adds a new 3D game world and initializes it.</summary>
        public virtual IGameWorldManager3D AddWorld(WorldIndex3D index, IPhysxWorld3D physx3D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager3D(index, physx3D, _partitioner, _prefabManager);
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

        public IGameWorldManager3D AddWorld(IWorldIndex3D index, IPhysxWorld3D physx3D)
        {
            return AddWorld(index, physx3D);
        }
    }
}
