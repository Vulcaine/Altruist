/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldOrganizer3D : IGameWorldOrganizer
    {
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
        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldLoader3D _worldLoader;
        private readonly Lazy<IVisibilityTracker?> _lazyVisibilityTracker;

        private IVisibilityTracker? _visibilityTracker => _lazyVisibilityTracker.Value;

        public GameWorldOrganizer3D(
            IWorldLoader3D worldLoader,
            IEnumerable<IWorldIndex3D> gameWorlds,
            IServiceProvider serviceProvider
        )
        {
            _worldLoader = worldLoader;
            // Break circular dep: resolve IVisibilityTracker lazily
            _lazyVisibilityTracker = new Lazy<IVisibilityTracker?>(
                () => serviceProvider.GetService(typeof(IVisibilityTracker)) as IVisibilityTracker);

            if (gameWorlds is null)
                throw new ArgumentNullException(nameof(gameWorlds));

            InitializeWorlds(gameWorlds).GetAwaiter().GetResult();
        }

        private async Task InitializeWorlds(IEnumerable<IWorldIndex3D> worlds)
        {
            foreach (var index in worlds)
            {
                var manager = await _worldLoader.LoadFromIndex(index);
                AddWorld(manager);
            }
        }

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

        public virtual void RemoveWorld(int index)
        {
            _worlds.Remove(index);
        }

        public virtual IGameWorldManager3D? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        public virtual IGameWorldManager3D? GetWorld(string name)
        {
            return _worlds
                .Where(x => x.Value.Index.Name == name)
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        public void Step(float deltaTime)
        {
            var steppedEngines = new HashSet<object>();
            var enginesToStep = new List<IPhysxWorld3D>();
            var objectsToSync = new List<IWorldObject3D>();

            foreach (var world in _worlds.Values)
            {
                try
                {
                    foreach (var obj in world.FindAllObjects<IWorldObject3D>())
                    {
                        if (obj.Expired)
                        {
                            world.DestroyObject(obj);
                            continue;
                        }

                        objectsToSync.Add(obj);

                        try
                        {
                            obj.Step(deltaTime, world);
                        }
                        catch
                        {
                        }
                    }

                    var physWorld = world.PhysxWorld;
                    var engine = physWorld.Engine;
                    if (engine != null && steppedEngines.Add(engine))
                    {
                        enginesToStep.Add(physWorld);
                    }
                }
                catch
                {
                }
            }

            foreach (var physWorld in enginesToStep)
            {
                try
                {
                    physWorld.Step(deltaTime);
                }
                catch
                {
                }
            }

            foreach (var obj in objectsToSync)
            {
                try
                {
                    SyncObjectFromPhysics(obj);
                }
                catch
                {
                }
            }

            // Compute visibility diffs after all positions are final
            if (_visibilityTracker is VisibilityTracker3D tracker)
            {
                try
                {
                    tracker.Tick();
                }
                catch
                {
                }
            }
        }

        private static void SyncObjectFromPhysics(IWorldObject3D obj)
        {
            if (obj.Body is not IPhysxBody3D body)
                return;

            var newPos = Position3D.From(body.Position);
            var newRot = Rotation3D.FromQuaternion(body.Rotation);

            obj.Transform = obj.Transform
                .WithPosition(newPos)
                .WithRotation(newRot);

            if (obj.Colliders != null)
            {
                foreach (var col in obj.Colliders)
                {
                    try
                    {
                        col.Transform = col.Transform
                            .WithPosition(newPos)
                            .WithRotation(newRot);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public bool Empty() => _worlds.Count == 0;

        public IEnumerable<IGameWorldManager3D> GetAllWorlds()
        {
            return _worlds.Values;
        }
    }
}
