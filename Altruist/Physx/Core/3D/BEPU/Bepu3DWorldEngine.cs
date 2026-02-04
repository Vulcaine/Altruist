/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;

using BepuUtilities;
using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD
{
    public struct WorldEngineCacheKey
    {
        public Vector3 Gravity;
        public float FixedDeltaTime;

        public WorldEngineCacheKey(Vector3 gravity, float fixedDeltaTime)
        {
            Gravity = gravity;
            FixedDeltaTime = fixedDeltaTime;
        }
    }

    [Service(typeof(IPhysxWorldEngineFactory3D))]
    public sealed class BepuWorldEngineFactory3D : IPhysxWorldEngineFactory3D
    {
        private readonly Dictionary<WorldEngineCacheKey, IPhysxWorldEngine3D> _cache = new();

        private readonly BepuHeightmapLoader _heightmapLoader;

        public BepuWorldEngineFactory3D(BepuHeightmapLoader heightmapLoader)
        {
            this._heightmapLoader = heightmapLoader;
        }

        public IPhysxWorldEngine3D GetExistingOrCreate(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            if (fixedDeltaTime <= 0f || float.IsNaN(fixedDeltaTime) || float.IsInfinity(fixedDeltaTime))
                fixedDeltaTime = 1f / 60f;

            var key = new WorldEngineCacheKey(gravity, fixedDeltaTime);
            if (_cache.TryGetValue(key, out var existing))
                return existing;

            var created = new BepuWorldEngine3D(_heightmapLoader, gravity, fixedDeltaTime);
            _cache[key] = created;
            return created;
        }
    }

    public sealed class BepuWorldEngine3D : IPhysxWorldEngine3D
    {
        public float FixedDeltaTime { get; }

        public IReadOnlyCollection<IPhysxBody3D> Bodies
        {
            get
            {
                lock (_sync)
                {
                    return _bodies.Values.ToArray();
                }
            }
        }

        private readonly BepuHeightmapLoader _heightmapLoader;

        internal Simulation Simulation => _simulation;

        private readonly Simulation _simulation;
        private readonly BufferPool _pool = new();

        private readonly Dictionary<string, IPhysxBody3D> _bodies = new();

        internal readonly object _sync = new();

        private readonly ConcurrentQueue<Action> _pending = new();

        private volatile bool _disposed;

        public BepuWorldEngine3D(
            BepuHeightmapLoader heightmapLoader,
            Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            this._heightmapLoader = heightmapLoader;
            if (fixedDeltaTime <= 0f || float.IsNaN(fixedDeltaTime) || float.IsInfinity(fixedDeltaTime))
                fixedDeltaTime = 1f / 60f;

            FixedDeltaTime = fixedDeltaTime;

            var narrow = new NarrowPhaseCallbacks();
            var pose = new PoseIntegratorCallbacks(gravity);
            var solve = new SolveDescription(16, 1);
            var stepper = new DefaultTimestepper();

            _simulation = Simulation.Create(_pool, narrow, pose, solve, stepper);
        }

        internal void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BepuWorldEngine3D));
        }

        private void Enqueue(Action action)
        {
            ThrowIfDisposed();
            _pending.Enqueue(action);
        }

        private void DrainPending_NoLock()
        {
            while (_pending.TryDequeue(out var action))
                action();
        }

        private float _accumulator;

        public void Step(float deltaTime)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                DrainPending_NoLock();

                deltaTime = MathF.Min(deltaTime, 0.25f);
                _accumulator += deltaTime;

                while (_accumulator >= FixedDeltaTime)
                {
                    _simulation.Timestep(FixedDeltaTime);
                    _accumulator -= FixedDeltaTime;
                }

                DrainPending_NoLock();
            }
        }

        public void AddBody(IPhysxBody3D body)
        {
            if (body is not Body3DAdapterBase)
                throw new InvalidOperationException("This engine can only add bodies created by the BEPU provider.");

            lock (_sync)
            {
                ThrowIfDisposed();

                var b = (Body3DAdapterBase)body;
                _bodies[b.Id] = body;
            }
        }

        public void RemoveBody(IPhysxBody3D body)
        {
            if (body is not Body3DAdapterBase b)
                return;

            Enqueue(() =>
            {
                if (!_bodies.Remove(b.Id))
                    return;

                b.MarkRemoved();

                if (b is DynamicBody3DAdapter dyn)
                {
                    _simulation.Bodies.Remove(dyn.Handle);
                }
                else if (b is StaticBody3DAdapter stat)
                {
                    _simulation.Statics.Remove(stat.Handle);
                }
            });
        }

        public IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1, uint layerMask = 0xFFFFFFFFu)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                var d = ray.To - ray.From;
                var maxT = d.Length();
                if (maxT <= 0f || maxHits <= 0)
                    return Array.Empty<PhysxRaycastHit3D>();

                d /= maxT;

                var collector = new QueryHitsCollector();
                _simulation.RayCast(ray.From, d, maxT, ref collector);

                return BuildFilteredResults(collector, maxHits, layerMask);
            }
        }

        public IEnumerable<PhysxRaycastHit3D> CapsuleCast(
            Vector3 center,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            int maxHits = 1,
            uint layerMask = 0xFFFFFFFFu)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                if (maxHits <= 0)
                    return Array.Empty<PhysxRaycastHit3D>();

                if (maxDistance <= 0f || float.IsNaN(maxDistance) || float.IsInfinity(maxDistance))
                    return Array.Empty<PhysxRaycastHit3D>();

                float dirLen = direction.Length();
                if (dirLen <= 1e-10f)
                    return Array.Empty<PhysxRaycastHit3D>();

                var dir = direction / dirLen;

                // BEPU capsule length parameter = distance between hemisphere centers
                float segmentLength = MathF.Max(0f, halfLength * 2f);
                var capsule = new Capsule(MathF.Max(0f, radius), segmentLength);

                // Upright capsule (Y axis)
                var pose = new RigidPose(center, Quaternion.Identity);

                // Sweep uses BodyVelocity; we use a *unit* velocity so maximumT is in distance units.
                var velocity = new BodyVelocity(dir, Vector3.Zero);

                var collector = new QueryHitsCollector();

                // Tuning knobs (stable defaults)
                const float minimumProgression = 1e-4f;
                const float convergenceThreshold = 1e-4f;
                const int maximumIterationCount = 16;

                _simulation.Sweep(
                    capsule,
                    pose,
                    velocity,
                    maxDistance,
                    _pool,              // BufferPool required by this overload
                    ref collector,
                    minimumProgression,
                    convergenceThreshold,
                    maximumIterationCount);

                return BuildFilteredResults(collector, maxHits, layerMask);
            }
        }

        private IEnumerable<PhysxRaycastHit3D> BuildFilteredResults(QueryHitsCollector collector, int maxHits, uint layerMask)
        {
            if (collector.Count == 0)
                return Array.Empty<PhysxRaycastHit3D>();

            collector.SortByT();

            var results = new List<PhysxRaycastHit3D>(Math.Min(maxHits, collector.Count));

            for (int i = 0; i < collector.Count && results.Count < maxHits; i++)
            {
                var h = collector.Hits[i];

                IPhysxBody3D? found = null;

                if (h.IsStatic)
                {
                    foreach (var b in _bodies.Values)
                    {
                        if (b is StaticBody3DAdapter stat && stat.Handle.Equals(h.Static))
                        {
                            found = stat;
                            break;
                        }
                    }
                }
                else
                {
                    foreach (var b in _bodies.Values)
                    {
                        if (b is DynamicBody3DAdapter dyn && dyn.Handle.Equals(h.Body))
                        {
                            found = dyn;
                            break;
                        }
                    }
                }

                if (found is not Body3DAdapterBase adapter)
                    continue;

                uint bodyMask = adapter.PhysxTag?.Layer ?? (uint)PhysxLayer.All;
                if ((bodyMask & layerMask) == 0u)
                    continue;

                results.Add(new PhysxRaycastHit3D(found, h.Point, h.Normal, h.T));
            }

            return results.Count == 0 ? Array.Empty<PhysxRaycastHit3D>() : results;
        }

        private struct QueryHitsCollector : IRayHitHandler, ISweepHitHandler
        {
            public struct Hit
            {
                public float T;
                public Vector3 Point;
                public Vector3 Normal;

                public bool IsStatic;
                public BodyHandle Body;
                public StaticHandle Static;

                public bool IsZeroT;
            }

            public List<Hit> Hits;
            public int Count => Hits?.Count ?? 0;

            // Shared "allow" filters (you can add child filtering later if needed)
            public bool AllowTest(CollidableReference collidable) => true;
            public bool AllowTest(CollidableReference collidable, int childIndex) => true;

            // ----------------------------
            // Ray hits
            // ----------------------------
            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
            {
                Hits ??= new List<Hit>(8);

                var point = ray.Origin + ray.Direction * t;

                if (collidable.Mobility == CollidableMobility.Static)
                {
                    Hits.Add(new Hit
                    {
                        T = t,
                        Point = point,
                        Normal = normal,
                        IsStatic = true,
                        Static = collidable.StaticHandle,
                        Body = default,
                        IsZeroT = false
                    });
                }
                else
                {
                    Hits.Add(new Hit
                    {
                        T = t,
                        Point = point,
                        Normal = normal,
                        IsStatic = false,
                        Body = collidable.BodyHandle,
                        Static = default,
                        IsZeroT = false
                    });
                }
            }

            // ----------------------------
            // Sweep hits
            // ----------------------------
            public void OnHit(ref float maximumT, float t, in Vector3 normal, in Vector3 hitLocation, CollidableReference collidable)
            {
                Hits ??= new List<Hit>(8);

                if (collidable.Mobility == CollidableMobility.Static)
                {
                    Hits.Add(new Hit
                    {
                        T = t,
                        Point = hitLocation,
                        Normal = normal,
                        IsStatic = true,
                        Static = collidable.StaticHandle,
                        Body = default,
                        IsZeroT = false
                    });
                }
                else
                {
                    Hits.Add(new Hit
                    {
                        T = t,
                        Point = hitLocation,
                        Normal = normal,
                        IsStatic = false,
                        Body = collidable.BodyHandle,
                        Static = default,
                        IsZeroT = false
                    });
                }
            }

            public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
            {
                // This means: we started overlapping something.
                // Still record it (useful for depenetration logic later), and clamp maximumT to 0 to early out.
                Hits ??= new List<Hit>(4);

                if (collidable.Mobility == CollidableMobility.Static)
                {
                    Hits.Add(new Hit
                    {
                        T = 0f,
                        Point = Vector3.Zero,
                        Normal = Vector3.Zero,
                        IsStatic = true,
                        Static = collidable.StaticHandle,
                        Body = default,
                        IsZeroT = true
                    });
                }
                else
                {
                    Hits.Add(new Hit
                    {
                        T = 0f,
                        Point = Vector3.Zero,
                        Normal = Vector3.Zero,
                        IsStatic = false,
                        Body = collidable.BodyHandle,
                        Static = default,
                        IsZeroT = true
                    });
                }

                maximumT = 0f;
            }

            public void SortByT()
            {
                if (Hits is null || Hits.Count <= 1)
                    return;

                Hits.Sort(static (a, b) => a.T.CompareTo(b.T));
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;

                DrainPending_NoLock();
                _bodies.Clear();

                _simulation.Dispose();
                _pool.Clear();
            }
        }

        internal TypedIndex CreateShapeIndexFromCollider(IPhysxCollider3D c)
        {
            var t = c.Transform;

            switch (c.Shape)
            {
                case PhysxColliderShape3D.Sphere3D:
                    {
                        var radius = t.Size.X;
                        return _simulation.Shapes.Add(new Sphere(radius));
                    }

                case PhysxColliderShape3D.Box3D:
                    {
                        var fullX = t.Size.X * 2f;
                        var fullY = t.Size.Y * 2f;
                        var fullZ = t.Size.Z * 2f;
                        return _simulation.Shapes.Add(new Box(fullX, fullY, fullZ));
                    }

                case PhysxColliderShape3D.Capsule3D:
                    {
                        var radius = t.Size.X;
                        var length = t.Size.Y * 2f;
                        return _simulation.Shapes.Add(new Capsule(radius, length));
                    }

                case PhysxColliderShape3D.Heightfield3D:
                    {
                        if (c.Heightfield is { } hf)
                        {
                            var mesh = _heightmapLoader.LoadHeightmapMesh(hf, _simulation.BufferPool);
                            return _simulation.Shapes.Add(mesh);
                        }

                        throw new InvalidOperationException("Heightmap collider has no HeightfieldData.");
                    }

                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }

        public abstract class Body3DAdapterBase : IPhysxBody3D
        {
            public string Id { get; }
            public abstract PhysxBodyType Type { get; set; }
            public abstract float Mass { get; set; }
            public PhysxTag? PhysxTag { get; set; }

            protected readonly BepuWorldEngine3D Engine;

            private readonly List<IPhysxCollider> _colliders = new();

            private volatile bool _removed;

            protected Body3DAdapterBase(string id, BepuWorldEngine3D engine)
            {
                Id = id;
                Engine = engine;
            }

            internal void MarkRemoved() => _removed = true;

            protected void ThrowIfRemovedOrDisposed()
            {
                Engine.ThrowIfDisposed();
                if (_removed)
                    throw new InvalidOperationException($"Body '{Id}' has been removed from the simulation.");
            }

            public virtual void AddCollider(IPhysxCollider collider)
            {
                if (collider is null)
                    throw new ArgumentNullException(nameof(collider));

                if (_colliders.Contains(collider))
                    return;

                _colliders.Add(collider);

                if (collider is IPhysxCollider3D c3)
                {
                    AttachCollider3D(c3);
                }
            }

            public virtual bool RemoveCollider(IPhysxCollider collider)
            {
                if (collider is null)
                    return false;

                var removed = _colliders.Remove(collider);

                if (removed && collider is IPhysxCollider3D c3)
                {
                    DetachCollider3D(c3);
                }

                return removed;
            }

            public ReadOnlySpan<IPhysxCollider> GetColliders() => CollectionsMarshal.AsSpan(_colliders);

            public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
            {
                if (string.IsNullOrEmpty(colliderId))
                {
                    collider = default!;
                    return false;
                }

                foreach (var c in _colliders)
                {
                    if (!string.IsNullOrEmpty(c.Id) &&
                        string.Equals(c.Id, colliderId, StringComparison.Ordinal))
                    {
                        collider = c;
                        return true;
                    }
                }

                collider = default!;
                return false;
            }

            public IPhysxCollider? GetColliderAt(int index)
            {
                if ((uint)index < (uint)_colliders.Count)
                    return _colliders[index];
                return null;
            }

            protected abstract void AttachCollider3D(IPhysxCollider3D collider);
            protected abstract void DetachCollider3D(IPhysxCollider3D collider);

            public abstract Vector3 Position { get; set; }
            public abstract Quaternion Rotation { get; set; }
            public abstract Vector3 LinearVelocity { get; set; }
            public abstract Vector3 AngularVelocity { get; set; }
            public abstract void ApplyForce(in PhysxForce force);
        }

        public sealed class DynamicBody3DAdapter : Body3DAdapterBase
        {
            public BodyHandle Handle => _handle;

            private BodyHandle _handle;
            private PhysxBodyType _type;
            private float _mass;

            private bool _hasOriginalShape;
            private TypedIndex _originalShape;

            public override PhysxBodyType Type
            {
                get => _type;
                set => _type = value;
            }

            public override float Mass
            {
                get => _mass;
                set => _mass = value;
            }

            public DynamicBody3DAdapter(
                string id,
                BepuWorldEngine3D engine,
                BodyHandle handle,
                PhysxBodyType type,
                float mass)
                : base(id, engine)
            {
                _handle = handle;
                _type = type;
                _mass = mass;
            }

            protected override void AttachCollider3D(IPhysxCollider3D collider)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    var bodyRef = Engine._simulation.Bodies.GetBodyReference(_handle);

                    if (!_hasOriginalShape)
                    {
                        _originalShape = bodyRef.Collidable.Shape;
                        _hasOriginalShape = true;
                    }

                    var shape = Engine.CreateShapeIndexFromCollider(collider);
                    bodyRef.Collidable.Shape = shape;

                    Engine._simulation.Awakener.AwakenBody(_handle);
                }
            }

            protected override void DetachCollider3D(IPhysxCollider3D collider)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    if (!_hasOriginalShape)
                        return;

                    var bodyRef = Engine._simulation.Bodies.GetBodyReference(_handle);
                    bodyRef.Collidable.Shape = _originalShape;

                    Engine._simulation.Awakener.AwakenBody(_handle);
                }
            }

            public override Vector3 Position
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        return Engine._simulation.Bodies.GetBodyReference(_handle).Pose.Position;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var br = Engine._simulation.Bodies.GetBodyReference(_handle);
                        br.Pose.Position = value;
                        Engine._simulation.Awakener.AwakenBody(_handle);
                    }
                }
            }

            public override Quaternion Rotation
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        return Engine._simulation.Bodies.GetBodyReference(_handle).Pose.Orientation;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var br = Engine._simulation.Bodies.GetBodyReference(_handle);
                        br.Pose.Orientation = value;
                        Engine._simulation.Awakener.AwakenBody(_handle);
                    }
                }
            }

            public override Vector3 LinearVelocity
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        return Engine._simulation.Bodies.GetBodyReference(_handle).Velocity.Linear;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var br = Engine._simulation.Bodies.GetBodyReference(_handle);
                        br.Velocity.Linear = value;
                        Engine._simulation.Awakener.AwakenBody(_handle);
                    }
                }
            }

            public override Vector3 AngularVelocity
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        return Engine._simulation.Bodies.GetBodyReference(_handle).Velocity.Angular;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var br = Engine._simulation.Bodies.GetBodyReference(_handle);
                        br.Velocity.Angular = value;
                        Engine._simulation.Awakener.AwakenBody(_handle);
                    }
                }
            }

            public override void ApplyForce(in PhysxForce force)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    var br = Engine._simulation.Bodies.GetBodyReference(_handle);

                    switch (force.Type)
                    {
                        case PhysxForce.Kind.AddForce3D:
                            br.ApplyLinearImpulse(force.Vector * Engine.FixedDeltaTime);
                            Engine._simulation.Awakener.AwakenBody(_handle);
                            break;

                        case PhysxForce.Kind.AddImpulse3D:
                            br.ApplyLinearImpulse(force.Vector);
                            Engine._simulation.Awakener.AwakenBody(_handle);
                            break;

                        case PhysxForce.Kind.AddTorque3D:
                            br.ApplyAngularImpulse(force.Vector);
                            Engine._simulation.Awakener.AwakenBody(_handle);
                            break;

                        case PhysxForce.Kind.SetLinearVelocity3D:
                            br.Velocity.Linear = force.Vector;
                            Engine._simulation.Awakener.AwakenBody(_handle);
                            break;

                        case PhysxForce.Kind.SetAngularVelocity3D:
                            br.Velocity.Angular = force.Vector;
                            Engine._simulation.Awakener.AwakenBody(_handle);
                            break;
                    }
                }
            }
        }

        public sealed class StaticBody3DAdapter : Body3DAdapterBase
        {
            public StaticHandle Handle => _handle;

            private StaticHandle _handle;

            private bool _hasOriginalShape;
            private TypedIndex _originalShape;

            public override PhysxBodyType Type
            {
                get => PhysxBodyType.Static;
                set { }
            }

            public override float Mass
            {
                get => 0f;
                set { }
            }

            public StaticBody3DAdapter(
                string id,
                BepuWorldEngine3D engine,
                StaticHandle handle)
                : base(id, engine)
            {
                _handle = handle;
            }

            protected override void AttachCollider3D(IPhysxCollider3D collider)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    var statics = Engine._simulation.Statics;
                    var sr = statics.GetStaticReference(_handle);

                    if (!_hasOriginalShape)
                    {
                        _originalShape = sr.Shape;
                        _hasOriginalShape = true;
                    }

                    var newShape = Engine.CreateShapeIndexFromCollider(collider);
                    var pose = sr.Pose;

                    statics.Remove(_handle);

                    var newDesc = new StaticDescription(pose.Position, pose.Orientation, newShape);
                    _handle = statics.Add(newDesc);
                }
            }

            protected override void DetachCollider3D(IPhysxCollider3D collider)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    if (!_hasOriginalShape)
                        return;

                    var statics = Engine._simulation.Statics;
                    var sr = statics.GetStaticReference(_handle);
                    var pose = sr.Pose;

                    statics.Remove(_handle);

                    var newDesc = new StaticDescription(pose.Position, pose.Orientation, _originalShape);
                    _handle = statics.Add(newDesc);
                }
            }

            public override Vector3 Position
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var sr = Engine._simulation.Statics.GetStaticReference(_handle);
                        return sr.Pose.Position;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var sr = Engine._simulation.Statics.GetStaticReference(_handle);
                        sr.Pose.Position = value;
                    }
                }
            }

            public override Quaternion Rotation
            {
                get
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var sr = Engine._simulation.Statics.GetStaticReference(_handle);
                        return sr.Pose.Orientation;
                    }
                }
                set
                {
                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        var sr = Engine._simulation.Statics.GetStaticReference(_handle);
                        sr.Pose.Orientation = value;
                    }
                }
            }

            public override Vector3 LinearVelocity
            {
                get => Vector3.Zero;
                set { }
            }

            public override Vector3 AngularVelocity
            {
                get => Vector3.Zero;
                set { }
            }

            public override void ApplyForce(in PhysxForce force) { }
        }

        private struct ClosestHitCollector : IRayHitHandler
        {
            public bool Hit;
            public float T;
            public Vector3 Point;
            public Vector3 Normal;

            public bool IsStatic;
            public BodyHandle Body;
            public StaticHandle Static;

            public bool AllowTest(CollidableReference collidable) => true;
            public bool AllowTest(CollidableReference collidable, int childIndex) => true;

            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
            {
                if (t >= maximumT)
                    return;

                maximumT = t;
                Hit = true;
                T = t;
                Normal = normal;
                Point = ray.Origin + ray.Direction * t;

                if (collidable.Mobility == CollidableMobility.Static)
                {
                    IsStatic = true;
                    Static = collidable.StaticHandle;
                    Body = default;
                }
                else
                {
                    IsStatic = false;
                    Body = collidable.BodyHandle;
                    Static = default;
                }
            }
        }

        private readonly struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
        {
            public void Initialize(Simulation simulation) { }
            public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => true;
            public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

            public bool ConfigureContactManifold<TManifold>(
                int workerIndex,
                CollidablePair pair,
                ref TManifold manifold,
                out PairMaterialProperties material)
                where TManifold : unmanaged, IContactManifold<TManifold>
            {
                material = new PairMaterialProperties
                {
                    FrictionCoefficient = 0.8f,
                    MaximumRecoveryVelocity = 2.0f,
                    SpringSettings = new SpringSettings(30f, 1f)
                };
                return true;
            }

            public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;
            public void Dispose() { }
        }

        private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
        {
            public Vector3 Gravity;

            public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
            public bool AllowSubstepsForUnconstrainedBodies => false;
            public bool IntegrateVelocityForKinematics => false;

            public PoseIntegratorCallbacks(Vector3 gravity) { Gravity = gravity; }

            public void Initialize(Simulation simulation) { }
            public void PrepareForIntegration(float dt) { }

            public void IntegrateVelocity(
                Vector<int> bodyIndices,
                Vector3Wide position,
                QuaternionWide orientation,
                BodyInertiaWide localInertia,
                Vector<int> integrationMask,
                int workerCount,
                Vector<float> dt,
                ref BodyVelocityWide velocity)
            {
                var g = Vector3Wide.Broadcast(Gravity);
                Vector3Wide.Scale(g, dt, out var gdt);
                velocity.Linear += gdt;
            }
        }
    }
}
