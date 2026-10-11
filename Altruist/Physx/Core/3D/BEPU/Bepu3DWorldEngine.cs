/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU implementation of <see cref="IPhysxWorldEngineFactory3D"/>, registered in DI as a stateless singleton. Every
    /// <see cref="Create"/> call returns a new, independent <see cref="BepuWorldEngine3D"/>.
    /// </summary>
    [Service(typeof(IPhysxWorldEngineFactory3D))]
    public sealed class BepuWorldEngineFactory3D : IPhysxWorldEngineFactory3D
    {
        /// <inheritdoc/>
        public IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
            => new BepuWorldEngine3D(gravity, fixedDeltaTime);
    }

    /// <summary>
    /// <see cref="IPhysxWorldEngine3D"/> backed by a BEPUphysics v2 <c>Simulation</c>. Create it through
    /// <see cref="IPhysxWorldEngineFactory3D"/> and create bodies with <see cref="BepuPhysxBodyApiProvider3D"/>; a body is
    /// part of the simulation, <see cref="Bodies"/> and every query from creation until <see cref="RemoveBody"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Stepping.</b> <see cref="Step"/> clamps the frame delta to 0.25 s, accumulates it and runs whole
    /// <see cref="FixedDeltaTime"/> timesteps (0, 1 or several per call). Solver: 16 velocity iterations, 1 substep.
    /// Gravity is added to every dynamic body's linear velocity each timestep; there is no damping. Continuous forces and
    /// torques (<see cref="PhysxForce.Kind.AddForce3D"/>, <see cref="PhysxForce.Kind.AddTorque3D"/>) act during every
    /// timestep of the next <see cref="Step"/> call that runs at least one timestep, then are cleared.</para>
    /// <para><b>Shapes.</b> A body's colliders form one BEPU compound (one child per collider). A body without colliders
    /// uses the descriptor's default box; a heightfield collider must be the only collider of a static or kinematic body.
    /// Colliders are placed at the body origin with the body's orientation: their transform's position and rotation are
    /// not applied. Replaced shapes are returned to the engine's buffer pool.</para>
    /// <para><b>Contacts.</b> Every pair collides (no layer filtering in the narrow phase) with fixed material
    /// properties: friction 0.8, maximum recovery velocity 2, contact spring (frequency 30, damping ratio 1). A pair in
    /// which either collider has <see cref="IPhysxCollider.IsTrigger"/> set produces no contact response.</para>
    /// <para><b>Events.</b> After every timestep the engine raises, on both colliders of each touching collider pair,
    /// <see cref="IPhysxCollider3D.OnCollisionEnter"/>/<see cref="IPhysxCollider3D.OnCollisionStay"/>/<see cref="IPhysxCollider3D.OnCollisionExit"/>
    /// for solid pairs, or <see cref="IPhysxCollider.OnTriggerEnter"/>/<see cref="IPhysxCollider.OnTriggerExit"/> for
    /// trigger pairs. Pairs whose non-static bodies are all asleep keep touching. Bodies without colliders raise nothing.
    /// Handlers run on the stepping thread inside <see cref="Step"/> (under the engine lock); their exceptions propagate
    /// out of <see cref="Step"/>.</para>
    /// <para><b>Threading.</b> The BEPU timestep runs on the calling thread (no thread dispatcher). All simulation access
    /// (stepping, queries, every body property get/set, collider attach) is serialised by one engine lock, so it is safe
    /// to call from several threads but they contend; a long <see cref="Step"/> blocks property reads.
    /// <see cref="RemoveBody"/> is lock-free and deferred to the next <see cref="Step"/>.</para>
    /// <para>Units: world units, seconds, radians; gravity is a per-second² vector supplied by the caller (typically −Y).</para>
    /// </remarks>
    public sealed partial class BepuWorldEngine3D : IPhysxWorldEngine3D
    {
        private const float SpeculativeMargin = 0.1f;
        private const float SleepThreshold = 0.01f;

        /// <inheritdoc/>
        public float FixedDeltaTime { get; }

        /// <summary>Snapshot (new array per call) of the live bodies of this engine, taken under the engine lock.</summary>
        public IReadOnlyCollection<IPhysxBody3D> Bodies
        {
            get
            {
                lock (_sync)
                {
                    return _bodies.Values.ToArray<IPhysxBody3D>();
                }
            }
        }

        internal Simulation Simulation => _simulation;

        private readonly Simulation _simulation;
        private readonly BufferPool _pool = new();

        private readonly Dictionary<string, Body3DAdapterBase> _bodies = new();
        private readonly Dictionary<BodyHandle, DynamicBody3DAdapter> _bodiesByHandle = new();
        private readonly Dictionary<StaticHandle, StaticBody3DAdapter> _staticsByHandle = new();
        private readonly Dictionary<BodyHandle, ContinuousLoad> _continuousLoads = new();
        private readonly ContactEventTracker _contacts;

        internal readonly object _sync = new();

        private readonly ConcurrentQueue<Action> _pending = new();

        private volatile bool _disposed;

        private float _accumulator;

        /// <summary>Creates an engine with its own buffer pool and simulation. In DI-built code prefer <see cref="IPhysxWorldEngineFactory3D.Create"/>.</summary>
        /// <param name="gravity">Gravity acceleration in units per second².</param>
        /// <param name="fixedDeltaTime">Fixed timestep in seconds; non-positive, NaN or infinite values fall back to 1/60.</param>
        public BepuWorldEngine3D(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            if (fixedDeltaTime <= 0f || float.IsNaN(fixedDeltaTime) || float.IsInfinity(fixedDeltaTime))
                fixedDeltaTime = 1f / 60f;

            FixedDeltaTime = fixedDeltaTime;

            _contacts = new ContactEventTracker(this);

            var narrow = new NarrowPhaseCallbacks(_contacts);
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

        /// <summary>
        /// Applies pending removals, advances the simulation by whole fixed timesteps covering
        /// <c>min(deltaTime, 0.25)</c> plus the carried-over remainder (raising contact events after each timestep), then
        /// applies removals queued meanwhile. Holds the engine lock for the whole call.
        /// </summary>
        /// <param name="deltaTime">Elapsed frame time in seconds.</param>
        /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
        public void Step(float deltaTime)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                DrainPending_NoLock();

                _accumulator += MathF.Min(deltaTime, 0.25f);

                bool stepped = false;
                while (_accumulator >= FixedDeltaTime)
                {
                    ApplyContinuousLoads_NoLock();
                    _simulation.Timestep(FixedDeltaTime);
                    _accumulator -= FixedDeltaTime;
                    stepped = true;
                    _contacts.Flush();
                }

                if (stepped)
                    _continuousLoads.Clear();

                DrainPending_NoLock();
            }
        }

        /// <summary>
        /// Confirms that <paramref name="body"/> belongs to this engine. Bodies join the simulation, <see cref="Bodies"/>
        /// and queries when <see cref="BepuPhysxBodyApiProvider3D.CreateBody"/> creates them, so this registers nothing
        /// new; it exists for the backend-agnostic <see cref="IPhysxWorldEngine3D"/> contract.
        /// </summary>
        /// <param name="body">BEPU body adapter.</param>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="body"/> was not created by the BEPU provider for this engine, or it was removed.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
        public void AddBody(IPhysxBody3D body)
        {
            var adapter = OwnBodyOrThrow(body);

            lock (_sync)
            {
                ThrowIfDisposed();

                if (!_bodies.TryGetValue(adapter.Id, out var registered) || !ReferenceEquals(registered, adapter))
                    throw new InvalidOperationException($"Body '{adapter.Id}' has been removed from this engine.");
            }
        }

        /// <summary>
        /// Queues removal of a body; it is removed from the simulation (and its shape released) at the start (or end) of
        /// the next <see cref="Step"/>, or on <see cref="Dispose"/>. Its colliders get exit events on the next timestep.
        /// After that, accessing the body's pose/velocity throws. Non-BEPU bodies and bodies already removed are ignored.
        /// </summary>
        /// <param name="body">Body to remove.</param>
        /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
        public void RemoveBody(IPhysxBody3D body)
        {
            if (body is not Body3DAdapterBase b || !ReferenceEquals(b.Engine, this))
                return;

            Enqueue(() => RemoveBody_NoLock(b));
        }

        private void RemoveBody_NoLock(Body3DAdapterBase body)
        {
            if (!_bodies.TryGetValue(body.Id, out var registered) || !ReferenceEquals(registered, body))
                return;

            _bodies.Remove(body.Id);
            body.MarkRemoved();

            switch (body)
            {
                case DynamicBody3DAdapter dyn:
                    _bodiesByHandle.Remove(dyn.Handle);
                    _continuousLoads.Remove(dyn.Handle);
                    _simulation.Bodies.Remove(dyn.Handle);
                    break;
                case StaticBody3DAdapter stat:
                    _staticsByHandle.Remove(stat.Handle);
                    _simulation.Statics.Remove(stat.Handle);
                    break;
            }

            DisposeShape(body.Shape);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Bodies outside <paramref name="layerMask"/> are skipped during the cast, so they never hide bodies behind them.
        /// Allocates per call.
        /// </remarks>
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

                var collector = new QueryHitsCollector(this, layerMask, ignored: null);
                _simulation.RayCast(ray.From, d, maxT, ref collector);

                return BuildResults(collector, maxHits);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Upright capsule (identity orientation) that can also hit the caller's own body; use
        /// <see cref="CapsuleCast(in PhysxCapsuleCast3D)"/> to tilt the capsule or exclude a body.
        /// </remarks>
        public IEnumerable<PhysxRaycastHit3D> CapsuleCast(
            Vector3 center,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            int maxHits = 1,
            uint layerMask = 0xFFFFFFFFu)
            => CapsuleCast(new PhysxCapsuleCast3D(center, radius, halfLength, direction, maxDistance)
            {
                MaxHits = maxHits,
                LayerMask = layerMask,
            });

        /// <inheritdoc/>
        /// <remarks>
        /// Implemented with a BEPU sweep (convergence 1e-4, max 16 iterations). The ignored body and bodies outside the
        /// layer mask are skipped during the sweep, so they never end it early.
        /// </remarks>
        public IEnumerable<PhysxRaycastHit3D> CapsuleCast(in PhysxCapsuleCast3D cast)
        {
            var orientationLength = cast.Orientation.Length();
            if (!float.IsFinite(orientationLength) || orientationLength <= 1e-6f)
                throw new ArgumentException("The capsule orientation must be a finite, non-zero quaternion.", nameof(cast));

            var ignored = cast.IgnoredBody is null ? null : OwnBodyOrThrow(cast.IgnoredBody);

            lock (_sync)
            {
                ThrowIfDisposed();

                if (cast.MaxHits <= 0)
                    return Array.Empty<PhysxRaycastHit3D>();

                if (cast.MaxDistance <= 0f || !float.IsFinite(cast.MaxDistance))
                    return Array.Empty<PhysxRaycastHit3D>();

                float dirLen = cast.Direction.Length();
                if (dirLen <= 1e-10f)
                    return Array.Empty<PhysxRaycastHit3D>();

                // BEPU's capsule length is the distance between the hemisphere centres.
                var capsule = new Capsule(MathF.Max(0f, cast.Radius), MathF.Max(0f, cast.HalfLength * 2f));
                var pose = new RigidPose(cast.Center, Quaternion.Normalize(cast.Orientation));

                // A unit velocity makes the sweep's maximumT a distance.
                var velocity = new BodyVelocity(cast.Direction / dirLen, Vector3.Zero);

                var collector = new QueryHitsCollector(this, cast.LayerMask, ignored);

                const float minimumProgression = 1e-4f;
                const float convergenceThreshold = 1e-4f;
                const int maximumIterationCount = 16;

                _simulation.Sweep(
                    capsule,
                    pose,
                    velocity,
                    cast.MaxDistance,
                    _pool,
                    ref collector,
                    minimumProgression,
                    convergenceThreshold,
                    maximumIterationCount);

                return BuildResults(collector, cast.MaxHits);
            }
        }

        private IEnumerable<PhysxRaycastHit3D> BuildResults(QueryHitsCollector collector, int maxHits)
        {
            if (collector.Hits.Count == 0)
                return Array.Empty<PhysxRaycastHit3D>();

            return collector.Hits
                .OrderBy(h => h.T)
                .Take(maxHits)
                .Select(h => new PhysxRaycastHit3D(FindBody(h.Collidable)!, h.Point, h.Normal, h.T))
                .ToArray();
        }

        private bool IsQueryable(CollidableReference collidable, uint layerMask, Body3DAdapterBase? ignored)
        {
            var body = FindBody(collidable);
            if (body is null || ReferenceEquals(body, ignored))
                return false;

            uint bodyMask = body.PhysxTag?.Layer ?? (uint)PhysxLayer.All;
            return (bodyMask & layerMask) != 0u;
        }

        internal Body3DAdapterBase? FindBody(CollidableReference collidable)
        {
            if (collidable.Mobility == CollidableMobility.Static)
                return _staticsByHandle.GetValueOrDefault(collidable.StaticHandle);

            return _bodiesByHandle.GetValueOrDefault(collidable.BodyHandle);
        }

        internal RigidPose PoseOf(CollidableReference collidable) =>
            collidable.Mobility == CollidableMobility.Static
                ? _simulation.Statics.GetStaticReference(collidable.StaticHandle).Pose
                : _simulation.Bodies.GetBodyReference(collidable.BodyHandle).Pose;

        private Body3DAdapterBase OwnBodyOrThrow(IPhysxBody3D body)
        {
            ArgumentNullException.ThrowIfNull(body);

            if (body is not Body3DAdapterBase adapter || !ReferenceEquals(adapter.Engine, this))
                throw new InvalidOperationException("This engine only accepts bodies created for it by the BEPU provider.");

            return adapter;
        }

        private readonly struct QueryHit
        {
            public float T { get; init; }
            public Vector3 Point { get; init; }
            public Vector3 Normal { get; init; }
            public CollidableReference Collidable { get; init; }
        }

        private readonly struct QueryHitsCollector : IRayHitHandler, ISweepHitHandler
        {
            private readonly BepuWorldEngine3D _engine;
            private readonly uint _layerMask;
            private readonly Body3DAdapterBase? _ignored;

            public List<QueryHit> Hits { get; }

            public QueryHitsCollector(BepuWorldEngine3D engine, uint layerMask, Body3DAdapterBase? ignored)
            {
                _engine = engine;
                _layerMask = layerMask;
                _ignored = ignored;
                Hits = new List<QueryHit>(8);
            }

            public bool AllowTest(CollidableReference collidable) => _engine.IsQueryable(collidable, _layerMask, _ignored);
            public bool AllowTest(CollidableReference collidable, int childIndex) => true;

            public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
                => Hits.Add(new QueryHit { T = t, Point = ray.Origin + ray.Direction * t, Normal = normal, Collidable = collidable });

            public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
                => Hits.Add(new QueryHit { T = t, Point = hitLocation, Normal = hitNormal, Collidable = collidable });

            // The sweep starts inside this collidable: record it and end the sweep, since nothing behind it is reachable.
            public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
            {
                Hits.Add(new QueryHit { T = 0f, Point = Vector3.Zero, Normal = Vector3.Zero, Collidable = collidable });
                maximumT = 0f;
            }
        }

        /// <summary>
        /// Applies queued removals, clears the body registry and disposes the BEPU simulation and buffer pool.
        /// Idempotent. Afterwards every engine and body call throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                DrainPending_NoLock();
                _disposed = true;

                _bodies.Clear();
                _bodiesByHandle.Clear();
                _staticsByHandle.Clear();
                _continuousLoads.Clear();

                _simulation.Dispose();
                _pool.Clear();
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        // Body creation
        // ---------------------------------------------------------------------------------------------------------

        internal Body3DAdapterBase CreateBody(in PhysxBody3DDesc desc)
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                if (string.IsNullOrEmpty(desc.Id))
                    throw new ArgumentException("A body descriptor needs an id.", nameof(desc));

                if (_bodies.ContainsKey(desc.Id))
                    throw new InvalidOperationException($"A body with id '{desc.Id}' already exists in this engine.");

                var pose = new RigidPose(desc.Transform.Position.ToVector3(), desc.Transform.Rotation.ToQuaternion());
                var defaultHalfExtents = desc.Transform.Size.ToVector3();

                Body3DAdapterBase body = desc.Type == PhysxBodyType.Static
                    ? CreateStatic(desc.Id, pose, defaultHalfExtents)
                    : CreateDynamic(desc, pose, defaultHalfExtents);

                body.PhysxTag = desc.PhysxTag;
                _bodies.Add(body.Id, body);
                return body;
            }
        }

        private StaticBody3DAdapter CreateStatic(string id, RigidPose pose, Vector3 defaultHalfExtents)
        {
            var shape = BuildShape(ImmutableArray<BepuCollider3D>.Empty, PhysxBodyType.Static, 0f, defaultHalfExtents);
            var handle = _simulation.Statics.Add(new StaticDescription(pose.Position, pose.Orientation, shape.Index));
            var body = new StaticBody3DAdapter(id, this, handle, shape.Index, defaultHalfExtents);
            _staticsByHandle.Add(handle, body);
            return body;
        }

        private DynamicBody3DAdapter CreateDynamic(in PhysxBody3DDesc desc, RigidPose pose, Vector3 defaultHalfExtents)
        {
            bool isKinematic = desc.IsKinematic || desc.Type == PhysxBodyType.Kinematic;
            var type = isKinematic ? PhysxBodyType.Kinematic : PhysxBodyType.Dynamic;
            var mass = desc.Mass > 0f ? desc.Mass : 1f;

            var shape = BuildShape(ImmutableArray<BepuCollider3D>.Empty, type, mass, defaultHalfExtents);
            var collidable = new CollidableDescription(shape.Index, SpeculativeMargin);
            var activity = new BodyActivityDescription(SleepThreshold);

            var bodyDesc = isKinematic
                ? BodyDescription.CreateKinematic(pose, collidable, activity)
                : BodyDescription.CreateDynamic(pose, shape.Inertia, collidable, activity);

            var handle = _simulation.Bodies.Add(bodyDesc);
            var body = new DynamicBody3DAdapter(desc.Id, this, handle, type, mass, shape.Index, defaultHalfExtents);
            _bodiesByHandle.Add(handle, body);
            return body;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Continuous forces
        // ---------------------------------------------------------------------------------------------------------

        private readonly record struct ContinuousLoad(Vector3 Force, Vector3 Torque);

        internal void AddContinuousLoad_NoLock(BodyHandle handle, Vector3 force, Vector3 torque)
        {
            var current = _continuousLoads.GetValueOrDefault(handle);
            _continuousLoads[handle] = new ContinuousLoad(current.Force + force, current.Torque + torque);
        }

        private void ApplyContinuousLoads_NoLock()
        {
            foreach (var (handle, load) in _continuousLoads)
            {
                var body = _simulation.Bodies.GetBodyReference(handle);
                body.ApplyLinearImpulse(load.Force * FixedDeltaTime);
                body.ApplyAngularImpulse(load.Torque * FixedDeltaTime);
                _simulation.Awakener.AwakenBody(handle);
            }
        }
    }
}
