/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/


using System.Runtime.CompilerServices;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldOrganizer3D : IGameWorldOrganizer
    {
        /// <summary>
        /// Directly registers an already constructed game world manager.
        /// </summary>
        IGameWorldManager3D AddWorld(IGameWorldManager3D manager);
        void RemoveWorld(int index);
        IGameWorldManager3D? GetWorld(int index);
        IGameWorldManager3D? GetWorld(string name);

        IEnumerable<IGameWorldManager3D> GetAllWorlds();
    }

    [Service(typeof(IGameWorldOrganizer))]
    [Service(typeof(IGameWorldOrganizer3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game")]
    public class GameWorldOrganizer3D : IGameWorldOrganizer3D
    {
        private sealed class StepGate
        {
            public int Running;
        }

        private readonly ConditionalWeakTable<IWorldObject3D, StepGate> _stepGates = new();

        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldLoader3D _worldLoader;
        private readonly IWorldPhysics3D _worldPhysx;

        public GameWorldOrganizer3D(
            IWorldLoader3D worldLoader,
            IWorldPhysics3D worldPhysics3D,
            IEnumerable<IWorldIndex3D> gameWorlds
            )
        {
            _worldLoader = worldLoader;
            _worldPhysx = worldPhysics3D;

            if (gameWorlds is null)
                throw new ArgumentNullException(nameof(gameWorlds));

            InitializeWorlds(gameWorlds).GetAwaiter();
        }

        private async Task InitializeWorlds(IEnumerable<IWorldIndex3D> worlds)
        {
            foreach (var index in worlds)
            {
                var manager = await _worldLoader.LoadFromIndex(index);
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
        public virtual IGameWorldManager3D? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        public virtual IGameWorldManager3D? GetWorld(string name)
        {
            return _worlds.Where(x => x.Value.Index.Name == name).Select(x => x.Value).FirstOrDefault();
        }

        /// <summary>
        /// Lists all currently loaded world indices.
        /// </summary>
        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        public async Task StepAsync(float deltaTime)
        {
            var steppedEngines = new HashSet<object>();

            foreach (var world in _worlds.Values)
            {
                try
                {
                    // Snapshot to avoid collection changing while we step in parallel
                    var objects = world.FindAllObjects<IWorldObject3D>().ToList();

                    var expired = new List<IWorldObject3D>(capacity: 8);
                    var tasks = new List<Task>(capacity: objects.Count);

                    foreach (var obj in objects)
                    {
                        if (obj.Expired)
                        {
                            expired.Add(obj);
                            continue;
                        }

                        tasks.Add(StepObjectOnceAsync(obj, deltaTime));
                    }

                    // Apply destroys outside parallel step execution
                    foreach (var obj in expired)
                    {
                        world.DestroyObject(obj);
                    }

                    // Run all object steps in parallel (each object is gated)
                    if (tasks.Count > 0)
                        await Task.WhenAll(tasks).ConfigureAwait(false);

                    // Step PhysX after object updates
                    var engine = world.PhysxWorld.Engine;
                    if (engine != null && steppedEngines.Add(engine))
                    {
                        world.PhysxWorld.Step(deltaTime);
                    }
                }
                catch
                {
                    // Swallow per-world exceptions
                }
            }
        }

        private async Task StepObjectOnceAsync(IWorldObject3D obj, float dt)
        {
            var gate = _stepGates.GetOrCreateValue(obj);

            // If already running, skip this tick
            if (Interlocked.Exchange(ref gate.Running, 1) == 1)
                return;

            try
            {
                await obj.StepAsync(dt, _worldPhysx).ConfigureAwait(false);
            }
            catch
            {
                // swallow per-object exceptions
            }
            finally
            {
                Volatile.Write(ref gate.Running, 0);
            }
        }

        public bool Empty() => _worlds.Count == 0;
        public IEnumerable<IGameWorldManager3D> GetAllWorlds()
        {
            return _worlds.Values;
        }
    }
}
