/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface IGameWorldOrganizer2D : IGameWorldOrganizer
    {
        IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D);
        void RemoveWorld(int index);
        IGameWorldManager2D? GetWorld(int index);
        IGameWorldManager2D? GetWorld(string name);
        IEnumerable<IGameWorldManager2D> GetAllWorlds();
    }

    [Service(typeof(IGameWorldOrganizer))]
    [Service(typeof(IGameWorldOrganizer2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public class GameWorldOrganizer2D : IGameWorldOrganizer2D
    {
        private readonly Dictionary<int, IGameWorldManager2D> _worlds = new();
        private readonly IWorldPartitioner2D _partitioner;
        private readonly ICacheProvider _cache;
        private readonly IPhysxWorldEngineFactory2D _physxWorldEngineFactory;
        private readonly IPhysxBodyApiProvider2D? _bodyApi;
        private readonly IPhysxColliderApiProvider2D? _colliderApi;
        private readonly IVisibilityTracker? _visibilityTracker;
        private readonly IAIBehaviorService? _aiBehaviorService;
        private readonly IEntitySyncService? _entitySyncService;
        private float _engineFrequencyHz = 25f;

        public GameWorldOrganizer2D(
            IWorldPartitioner2D partitioner,
            ICacheProvider cache,
            IPhysxWorldEngineFactory2D physxWorldEngineFactory,
            IEnumerable<IWorldIndex2D> gameWorlds,
            IPhysxBodyApiProvider2D? bodyApi = null,
            IPhysxColliderApiProvider2D? colliderApi = null,
            IVisibilityTracker? visibilityTracker = null,
            IAIBehaviorService? aiBehaviorService = null,
            IEntitySyncService? entitySyncService = null)
        {
            _partitioner = partitioner;
            _cache = cache;
            _physxWorldEngineFactory = physxWorldEngineFactory;
            _bodyApi = bodyApi;
            _colliderApi = colliderApi;
            _visibilityTracker = visibilityTracker;
            _aiBehaviorService = aiBehaviorService;
            _entitySyncService = entitySyncService;
            _worlds = gameWorlds
                .Select(index2d => AddWorld(
                    index2d,
                    new PhysxWorld2D(_physxWorldEngineFactory.Create(index2d.Gravity, index2d.FixedDeltaTime))))
                .ToDictionary(x => x.Index.Index);
        }

        /// <summary>Adds a new game world and initializes it.</summary>
        public virtual IGameWorldManager2D AddWorld(IWorldIndex2D index, IPhysxWorld2D physx2D)
        {
            if (_worlds.ContainsKey(index.Index))
                throw new InvalidOperationException($"World {index.Index} already exists.");

            var manager = new GameWorldManager2D(index, physx2D, _partitioner, _cache, _bodyApi, _colliderApi);
            manager.Initialize();
            _worlds[index.Index] = manager;
            return manager;
        }

        /// <summary>Removes the specified world by index.</summary>
        public virtual void RemoveWorld(int index)
        {
            _worlds.Remove(index);
        }

        public virtual IGameWorldManager2D? GetWorld(int index)
        {
            return _worlds.TryGetValue(index, out var manager) ? manager : null;
        }

        public virtual IGameWorldManager2D? GetWorld(string name)
        {
            return _worlds.Values.FirstOrDefault(w => w.Index.Name == name);
        }

        public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;

        public virtual IEnumerable<IGameWorldManager2D> GetAllWorlds() => _worlds.Values;

        public void Step(float deltaTime)
        {
            var steppedEngines = new HashSet<object>();
            var enginesToStep = new List<IPhysxWorld2D>();
            var objectsToSync = new List<IWorldObject2D>();

            foreach (var world in _worlds.Values)
            {
                try
                {
                    foreach (var obj in world.FindAllObjects<IWorldObject2D>())
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

                    if (world.PhysxWorld is IPhysxWorld2D physWorld2D && steppedEngines.Add(physWorld2D))
                        enginesToStep.Add(physWorld2D);
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

            // Build dimension-agnostic snapshots for shared services
            var worldSnapshots = new WorldSnapshot[_worlds.Count];
            int snapIdx = 0;
            foreach (var world in _worlds.Values)
            {
                var objs = world.GetAllObjects().Cast<ITypelessWorldObject>().ToList();
                var lookup = objs.ToDictionary(o => o.InstanceId, o => o);
                worldSnapshots[snapIdx++] = new WorldSnapshot(world.Index.Index, objs, lookup);
            }

            // AI behaviors tick (after physics, before visibility/sync)
            if (_aiBehaviorService != null)
            {
                try { _aiBehaviorService.Tick(worldSnapshots, deltaTime); }
                catch { }
            }

            // Compute visibility diffs after all positions are final
            if (_visibilityTracker is VisibilityTracker2D tracker)
            {
                try { tracker.Tick(); }
                catch { }
            }

            // Auto-sync [Synchronized] entities (delta-based, after visibility)
            if (_entitySyncService != null)
            {
                try { _entitySyncService.Tick(worldSnapshots, _engineFrequencyHz).GetAwaiter().GetResult(); }
                catch { }
            }
        }

        private static void SyncObjectFromPhysics(IWorldObject2D obj)
        {
            if (obj.Body is not IPhysxBody2D body)
                return;

            var newPos = Position2D.Of((int)body.Position.X, (int)body.Position.Y);
            obj.Transform = obj.Transform.WithPosition(newPos);
        }

        public bool Empty() => _worlds.Count == 0;
    }
}
