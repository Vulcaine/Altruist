// BepuWorldEngine3D.cs  (BEPU engine; implements AddBody/RemoveBody; NO creation API)
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
    public sealed class BepuWorldEngineFactory3D : IPhysxWorldEngineFactory3D
    {
        public IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
            => new BepuWorldEngine3D(gravity, fixedDeltaTime);
    }

    public class BepuWorldEngine3D : IPhysxWorldEngine3D
    {
        public float FixedDeltaTime { get; }
        public IReadOnlyCollection<IPhysxBody3D> Bodies => _bodies.Values.Cast<IPhysxBody3D>().ToList();

        internal Simulation Simulation => _simulation;

        private readonly Simulation _simulation;
        private readonly BufferPool _pool = new();
        private readonly Dictionary<string, Body3DAdapter> _bodies = new();

        public BepuWorldEngine3D(Vector3 gravity, float fixedDeltaTime = 1f / 60f)
        {
            FixedDeltaTime = fixedDeltaTime;

            var narrow = new NarrowPhaseCallbacks();
            var pose = new PoseIntegratorCallbacks(gravity);
            var solve = new SolveDescription(8, 1);
            var stepper = new DefaultTimestepper();

            _simulation = Simulation.Create(_pool, narrow, pose, solve, stepper);
        }

        public void Step(float deltaTime) => _simulation.Timestep(deltaTime);

        public void AddBody(IPhysxBody3D body)
        {
            if (body is not Body3DAdapter adapter)
                throw new InvalidOperationException("This engine can only add bodies created by the BEPU provider.");

            // Body is already in the simulation (created by provider). Just register it locally.
            _bodies[adapter.Id] = adapter;
        }

        public void RemoveBody(IPhysxBody3D body)
        {
            if (body is Body3DAdapter b && _bodies.Remove(b.Id))
                _simulation.Bodies.Remove(b.Handle);
        }

        public IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1)
        {
            var d = ray.To - ray.From;
            var maxT = d.Length();
            if (maxT <= 0f)
                return Array.Empty<PhysxRaycastHit3D>();
            d /= maxT;

            var collector = new ClosestHitCollector();
            _simulation.RayCast(ray.From, d, maxT, ref collector);
            if (!collector.Hit)
                return Array.Empty<PhysxRaycastHit3D>();

            var found = _bodies.Values.FirstOrDefault(b => b.Handle.Equals(collector.Body));
            if (found == null)
                return Array.Empty<PhysxRaycastHit3D>();

            return new[] { new PhysxRaycastHit3D(found, collector.Point, collector.Normal, collector.T) };
        }

        public void Dispose()
        {
            _simulation.Dispose();
            _pool.Clear();
        }

        public sealed class Body3DAdapter : IPhysxBody3D
        {
            public string Id { get; }
            public PhysxBodyType Type { get; set; }
            public float Mass { get => _mass; set => _mass = value; }
            public object? UserData { get; set; }

            public Vector3 Position
            {
                get => _engine.Simulation.Bodies.GetBodyReference(_handle).Pose.Position;
                set { var br = _engine.Simulation.Bodies.GetBodyReference(_handle); br.Pose.Position = value; }
            }

            public Quaternion Rotation
            {
                get => _engine.Simulation.Bodies.GetBodyReference(_handle).Pose.Orientation;
                set { var br = _engine.Simulation.Bodies.GetBodyReference(_handle); br.Pose.Orientation = value; }
            }

            public Vector3 LinearVelocity
            {
                get => _engine.Simulation.Bodies.GetBodyReference(_handle).Velocity.Linear;
                set { var br = _engine.Simulation.Bodies.GetBodyReference(_handle); br.Velocity.Linear = value; }
            }

            public Vector3 AngularVelocity
            {
                get => _engine.Simulation.Bodies.GetBodyReference(_handle).Velocity.Angular;
                set { var br = _engine.Simulation.Bodies.GetBodyReference(_handle); br.Velocity.Angular = value; }
            }

            public BodyHandle Handle => _handle;

            private readonly BepuWorldEngine3D _engine;
            private readonly BodyHandle _handle;
            private readonly List<IPhysxCollider> _colliders = new();
            private float _mass;

            public Body3DAdapter(string id, BepuWorldEngine3D engine, BodyHandle handle, PhysxBodyType type, float mass)
            {
                Id = id;
                _engine = engine;
                _handle = handle;
                Type = type;
                _mass = mass;
            }

            public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);
            public bool RemoveCollider(IPhysxCollider collider) => _colliders.Remove(collider);
            public ReadOnlySpan<IPhysxCollider> GetColliders() => CollectionsMarshal.AsSpan(_colliders);

            public void ApplyForce(in PhysxForce force)
            {
                var br = _engine.Simulation.Bodies.GetBodyReference(_handle);
                switch (force.Type)
                {
                    case PhysxForce.Kind.AddForce3D:
                        br.ApplyLinearImpulse(force.Vector * _engine.FixedDeltaTime);
                        break;
                    case PhysxForce.Kind.AddImpulse3D:
                        br.ApplyLinearImpulse(force.Vector);
                        break;
                    case PhysxForce.Kind.AddTorque3D:
                        br.ApplyAngularImpulse(force.Vector);
                        break;
                    case PhysxForce.Kind.SetLinearVelocity3D:
                        br.Velocity.Linear = force.Vector;
                        break;
                    case PhysxForce.Kind.SetAngularVelocity3D:
                        br.Velocity.Angular = force.Vector;
                        break;
                }
            }

            public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
            {
                if (string.IsNullOrEmpty(colliderId))
                {
                    collider = default!;
                    return false;
                }

                foreach (var c in _colliders)
                {
                    // If the concrete collider type exposes an Id (common for adapters), use it.
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

        }

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
                    Body = collidable.Mobility == CollidableMobility.Dynamic ? collidable.BodyHandle : default;
                }
            }
        }

        private readonly struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
        {
            public void Initialize(Simulation simulation) { }

            public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => true;

            public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

            public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties material)
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
