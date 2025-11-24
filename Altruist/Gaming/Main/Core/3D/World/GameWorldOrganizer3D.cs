/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldCoordinator3D : IGameWorldOrganizer
    {
        /// <summary>
        /// Directly registers an already constructed game world manager.
        /// </summary>
        IGameWorldManager3D AddWorld(IGameWorldManager3D manager);
    }

    [Service(typeof(IGameWorldCoordinator3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public class GameWorldOrganizer3D : IGameWorldCoordinator3D
    {
        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldLoader3D _worldLoader;

        public GameWorldOrganizer3D(
            IWorldLoader3D worldLoader,
            IEnumerable<WorldIndex3D> gameWorlds)
        {
            _worldLoader = worldLoader ?? throw new ArgumentNullException(nameof(worldLoader));

            if (gameWorlds is null)
                throw new ArgumentNullException(nameof(gameWorlds));

            foreach (var index in gameWorlds)
            {
                var manager = _worldLoader.LoadFromIndex(index);
                AddWorld(manager);
            }
        }

        /// <summary>
        /// Registers an existing GameWorldManager3D.
        /// </summary>
        public IGameWorldManager3D AddWorld(IGameWorldManager3D manager)
        {
            if (manager is null)
                throw new ArgumentNullException(nameof(manager));

            var idx = manager.Index.Index;
            if (_worlds.ContainsKey(idx))
                throw new InvalidOperationException($"World {idx} already exists.");

            _worlds[idx] = manager;
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
                    // Intentionally swallow step exceptions per-world
                }
            }
        }

        public bool Empty() => _worlds.Count == 0;
    }
}
