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
        void SetVisibilityTracker(IVisibilityTracker? tracker);
    }

    [Service(typeof(IGameWorldOrganizer))]
    [Service(typeof(IGameWorldOrganizer3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game")]
    public class GameWorldOrganizer3D : IGameWorldOrganizer3D
    {
        private readonly Dictionary<int, IGameWorldManager3D> _worlds = new();
        private readonly IWorldLoader3D _worldLoader;
        private readonly IEntitySyncService? _entitySyncService;
        private readonly IAIBehaviorService? _aiBehaviorService;
        private readonly IPositionHistoryRecorder? _positionRecorder;
        private IVisibilityTracker? _visibilityTracker;
        private float _engineFrequencyHz = 25f;

        public GameWorldOrganizer3D(
            IWorldLoader3D worldLoader,
            IEnumerable<IWorldIndex3D> gameWorlds,
            IEntitySyncService? entitySyncService = null,
            IAIBehaviorService? aiBehaviorService = null,
            IPositionHistoryRecorder? positionRecorder = null
        )
        {
            _worldLoader = worldLoader;
            _entitySyncService = entitySyncService;
            _aiBehaviorService = aiBehaviorService;
            _positionRecorder = positionRecorder;

            if (gameWorlds is null)
                throw new ArgumentNullException(nameof(gameWorlds));

            // Initialize worlds synchronously — LoadFromIndex returns immediately
            // for worlds without a DataPath (creates empty physics world).
            foreach (var index in gameWorlds)
            {
                var manager = _worldLoader.LoadFromIndex(index).GetAwaiter().GetResult();
                AddWorld(manager);
            }
        }

        public void SetVisibilityTracker(IVisibilityTracker? tracker)
        {
            _visibilityTracker = tracker;
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
            var worlds = _worlds.Values.ToArray();

            if (worlds.Length <= 1)
            {
                foreach (var world in worlds)
                    StepWorld(world, deltaTime);
            }
            else
            {
                Parallel.ForEach(worlds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    world => StepWorld(world, deltaTime));
            }

            _positionRecorder?.RecordSnapshot(Altruist.Engine.AltruistEngine.CurrentTick);

            var worldSnapshots = new WorldSnapshot[worlds.Length];
            for (int i = 0; i < worlds.Length; i++)
            {
                var (list, lookup) = worlds[i].GetCachedSnapshot();
                var typelessList = (IReadOnlyList<ITypelessWorldObject>)list;
                // Dictionary is invariant on TValue — cannot cast directly.
                // Wrap with a covariant read-only view.
                var typelessLookup = new Dictionary<string, ITypelessWorldObject>(lookup.Count);
                foreach (var kvp in lookup)
                    typelessLookup[kvp.Key] = kvp.Value;
                worldSnapshots[i] = new WorldSnapshot(worlds[i].Index.Index, typelessList, typelessLookup);
            }

            if (_aiBehaviorService != null)
            {
                try { _aiBehaviorService.Tick(worldSnapshots, deltaTime); }
                catch { }
            }

            var visTask = Task.CompletedTask;
            if (_visibilityTracker is VisibilityTracker3D tracker)
            {
                visTask = Task.Run(() =>
                {
                    try { tracker.Tick(worldSnapshots); }
                    catch (Exception ex)
                    {
                        System.Console.Error.WriteLine($"[VISIBILITY ERROR] {ex.GetType().Name}: {ex.Message}");
                        System.Console.Error.WriteLine(ex.StackTrace?.Split('\n')[0]);
                    }
                });
            }

            if (_entitySyncService != null)
            {
                try { _entitySyncService.Tick(worldSnapshots, _engineFrequencyHz).GetAwaiter().GetResult(); }
                catch { }
            }

            visTask.GetAwaiter().GetResult();
        }

        private static void StepWorld(IGameWorldManager3D world, float deltaTime)
        {
            try
            {
                var objectsToSync = AltruistPool.RentList<IWorldObject3D>();

                foreach (var obj in world.FindAllObjects<IWorldObject3D>())
                {
                    if (obj.Expired)
                    {
                        world.DestroyObject(obj);
                        continue;
                    }

                    try { obj.Step(deltaTime, world); }
                    catch { }

                    objectsToSync.Add(obj);
                }

                // Physics step (if enabled)
                var physWorld = world.PhysxWorld;
                if (physWorld?.Engine != null)
                {
                    try { physWorld.Step(deltaTime); }
                    catch { }
                }

                // Sync physics -> transform
                foreach (var obj in objectsToSync)
                {
                    try { SyncObjectFromPhysics(obj); }
                    catch { }
                }

                AltruistPool.ReturnList(objectsToSync);
            }
            catch { }
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
