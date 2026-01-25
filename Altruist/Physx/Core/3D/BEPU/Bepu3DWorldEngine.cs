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

        public IPhysxWorldEngine3D GetExistingOrCreate(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            var key = new WorldEngineCacheKey(gravity, fixedDeltaTime);
            if (_cache.TryGetValue(key, out var existing))
                return existing;

            var created = new BepuWorldEngine3D(gravity, fixedDeltaTime);
            _cache[key] = created;
            return created;
        }
    }

    public sealed class BepuWorldEngine3D : IPhysxWorldEngine3D
    {
        public float FixedDeltaTime { get; }

        // Thread-safe snapshot
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

        internal Simulation Simulation => _simulation;

        private readonly Simulation _simulation;
        private readonly BufferPool _pool = new();

        private readonly Dictionary<string, IPhysxBody3D> _bodies = new();

        // Single lock protecting ALL BEPU + body registry usage
        internal readonly object _sync = new();

        // Any operation touching BEPU must go through this queue + Step
        private readonly ConcurrentQueue<Action> _pending = new();

        private volatile bool _disposed;

        public BepuWorldEngine3D(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            FixedDeltaTime = fixedDeltaTime;

            var narrow = new NarrowPhaseCallbacks();
            var pose = new PoseIntegratorCallbacks(gravity);
            var solve = new SolveDescription(8, 1);
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
            // Caller must hold _sync.
            while (_pending.TryDequeue(out var action))
                action();
        }

        private float _debugPrintAccum = 0f;
        private const float DebugPrintIntervalSeconds = 2f;
        public void Step(float deltaTime)
        {
            _debugPrintAccum += deltaTime;

            if (_debugPrintAccum >= DebugPrintIntervalSeconds)
            {
                _debugPrintAccum = 0f;

                lock (_sync)
                {
                    ThrowIfDisposed();

                    Console.WriteLine($"BeforeStep RegisteredBodies={_bodies.Count}");
                    Console.WriteLine($"BeforeStep Active={_simulation.Bodies.ActiveSet}");
                    Console.WriteLine($"Active Statics={_simulation.Statics.Count}");

                    Console.WriteLine("---- Dynamic/Kinematic Bodies ----");
                    foreach (var kvp in _bodies)
                    {
                        var id = kvp.Key;
                        var b = kvp.Value;

                        if (b is DynamicBody3DAdapter dyn)
                        {
                            var br = _simulation.Bodies.GetBodyReference(dyn.Handle);
                            var p = br.Pose.Position;
                            var v = br.Velocity.Linear;

                            Console.WriteLine(
                                $"Body {id} [{dyn.Type}] Pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.00}) Vel=({v.X:0.00},{v.Y:0.00},{v.Z:0.00})"
                            );
                        }
                        else if (b is StaticBody3DAdapter stat)
                        {
                            var sr = _simulation.Statics.GetStaticReference(stat.Handle);
                            var p = sr.Pose.Position;

                            Console.WriteLine(
                                $"Static {id} Pos=({p.X:0.00},{p.Y:0.00},{p.Z:0.00})"
                            );
                        }
                    }

                    Console.WriteLine("------------------------------");
                }
            }

            lock (_sync)
            {
                ThrowIfDisposed();

                DrainPending_NoLock();
                _simulation.Timestep(deltaTime);
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

            // Don’t touch BEPU immediately; queue for Step() so it never races timestep.
            Enqueue(() =>
            {
                if (!_bodies.Remove(b.Id))
                    return;

                b.MarkRemoved();

                // Remove from correct BEPU container
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

        public IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                var d = ray.To - ray.From;
                var maxT = d.Length();
                if (maxT <= 0f)
                    return Array.Empty<PhysxRaycastHit3D>();

                d /= maxT;

                var collector = new ClosestHitCollector();
                _simulation.RayCast(ray.From, d, maxT, ref collector);

                if (!collector.Hit)
                    return Array.Empty<PhysxRaycastHit3D>();

                // Ray collector only returns BodyHandle for dynamic/kinematic.
                // Statics are hit too, but their handle is "default", so we can’t map them back here.
                if (collector.Body.Value == 0)
                    return Array.Empty<PhysxRaycastHit3D>();

                IPhysxBody3D? found = null;

                foreach (var b in _bodies.Values)
                {
                    if (b is DynamicBody3DAdapter dyn && dyn.Handle.Equals(collector.Body))
                    {
                        found = dyn;
                        break;
                    }
                }

                if (found == null)
                    return Array.Empty<PhysxRaycastHit3D>();

                return new[]
                {
                    new PhysxRaycastHit3D(found, collector.Point, collector.Normal, collector.T)
                };
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

        // ============================================================
        // ✅ BODY ADAPTERS (Base + Dynamic + Static)
        // ============================================================

        /// <summary>
        /// Common base adapter shared by dynamic + static bodies.
        /// Holds only engine link + id + colliders + removal state + collider utilities.
        /// </summary>
        public abstract class Body3DAdapterBase : IPhysxBody3D
        {
            public string Id { get; }
            public abstract PhysxBodyType Type { get; set; }
            public abstract float Mass { get; set; }
            public object? UserData { get; set; }

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

            public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);
            public bool RemoveCollider(IPhysxCollider collider) => _colliders.Remove(collider);
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
                    var idProp = c.GetType().GetProperty(
                        "Id",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);

                    if (idProp != null && idProp.GetValue(c) is string id &&
                        string.Equals(id, colliderId, StringComparison.Ordinal))
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

            public abstract Vector3 Position { get; set; }
            public abstract Quaternion Rotation { get; set; }
            public abstract Vector3 LinearVelocity { get; set; }
            public abstract Vector3 AngularVelocity { get; set; }
            public abstract void ApplyForce(in PhysxForce force);
        }

        /// <summary>
        /// Adapter for BEPU dynamic/kinematic bodies stored in Simulation.Bodies
        /// </summary>
        public sealed class DynamicBody3DAdapter : Body3DAdapterBase
        {
            public BodyHandle Handle => _handle;

            private BodyHandle _handle;
            private PhysxBodyType _type;
            private float _mass;

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

                        // if you teleport a body, waking helps immediate contact solve
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

                        // ✅ critical: setting velocity does NOT always wake sleeping bodies
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

        /// <summary>
        /// Adapter for BEPU statics stored in Simulation.Statics
        /// </summary>
        public sealed class StaticBody3DAdapter : Body3DAdapterBase
        {
            public StaticHandle Handle => _handle;

            private StaticHandle _handle;

            public override PhysxBodyType Type
            {
                get => PhysxBodyType.Static;
                set { /* ignored */ }
            }

            public override float Mass
            {
                get => 0f;
                set { /* ignored */ }
            }

            public StaticBody3DAdapter(
                string id,
                BepuWorldEngine3D engine,
                StaticHandle handle)
                : base(id, engine)
            {
                _handle = handle;
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
                set { /* statics don’t move */ }
            }

            public override Vector3 AngularVelocity
            {
                get => Vector3.Zero;
                set { /* statics don’t rotate */ }
            }

            public override void ApplyForce(in PhysxForce force)
            {
                // Static bodies ignore forces
            }
        }

        // ============================================================
        // Raycast collector (unchanged)
        // ============================================================

        private struct ClosestHitCollector : IRayHitHandler
        {
            public bool Hit;
            public float T;
            public Vector3 Point;
            public Vector3 Normal;
            public BodyHandle Body;

            public bool AllowTest(CollidableReference collidable) => true;
            public bool AllowTest(CollidableReference collidable, int childIndex) => true;

            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
            {
                if (t < maximumT)
                {
                    maximumT = t;
                    Hit = true;
                    T = t;
                    Normal = normal;
                    Point = ray.Origin + ray.Direction * t;

                    // Dynamic/kinematic -> BodyHandle, static -> default
                    Body = collidable.Mobility == CollidableMobility.Dynamic
                        ? collidable.BodyHandle
                        : default;
                }
            }
        }

        // ============================================================
        // Narrowphase + Integrator callbacks (unchanged)
        // ============================================================

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
                    FrictionCoefficient = 0.5f,
                    MaximumRecoveryVelocity = 2f,
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
