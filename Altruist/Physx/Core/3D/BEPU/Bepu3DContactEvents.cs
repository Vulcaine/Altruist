/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Runtime.CompilerServices;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;

using BepuUtilities;

namespace Altruist.Physx.ThreeD
{
    public sealed partial class BepuWorldEngine3D
    {
        /// <summary>One touching collider pair seen in a timestep. <see cref="Normal"/> points from B to A.</summary>
        private sealed record ContactRecord(
            BepuCollider3D ColliderA,
            BepuCollider3D ColliderB,
            Body3DAdapterBase BodyA,
            Body3DAdapterBase BodyB,
            Vector3 Point,
            Vector3 Normal,
            float Depth,
            bool IsTrigger)
        {
            public ContactPairKey Key => new(ColliderA, ColliderB, IsTrigger);
        }

        /// <summary>Colliders in id order, so a pair has one key whichever order BEPU reports it in.</summary>
        private readonly record struct ContactPairKey(BepuCollider3D A, BepuCollider3D B, bool IsTrigger);

        private enum ContactPhase
        {
            Enter,
            Stay,
            Exit,
        }

        /// <summary>
        /// Collects the touching collider pairs reported by the narrow phase during a timestep and, in
        /// <see cref="Flush"/>, turns the difference to the previous timestep into collider events. Runs on the stepping
        /// thread only (the simulation has no thread dispatcher).
        /// </summary>
        private sealed class ContactEventTracker
        {
            private readonly BepuWorldEngine3D _engine;

            private Dictionary<ContactPairKey, ContactRecord> _current = new();
            private Dictionary<ContactPairKey, ContactRecord> _previous = new();

            public ContactEventTracker(BepuWorldEngine3D engine) => _engine = engine;

            /// <summary>
            /// Records the child pair when both children are colliders and touch, and empties the manifold when either is
            /// a trigger so the pair produces no contact constraint.
            /// </summary>
            public void OnChildManifold(in CollidablePair pair, int childA, int childB, ref ConvexContactManifold manifold)
            {
                var bodyA = _engine.FindBody(pair.A);
                var bodyB = _engine.FindBody(pair.B);
                var colliderA = ColliderOf(bodyA, childA);
                var colliderB = ColliderOf(bodyB, childB);

                bool isTrigger = (colliderA?.IsTrigger ?? false) || (colliderB?.IsTrigger ?? false);

                if (colliderA is not null && colliderB is not null && TryDeepestContact(ref manifold, out var offset, out var depth))
                {
                    var poseA = _engine.PoseOf(pair.A);
                    var childOffset = QuaternionEx.Transform(LocalPose(colliderA).Position, poseA.Orientation);
                    var point = poseA.Position + childOffset + offset;
                    Record(new ContactRecord(colliderA, colliderB, bodyA!, bodyB!, point, manifold.Normal, depth, isTrigger));
                }

                if (isTrigger)
                    manifold.Count = 0;
            }

            /// <summary>Raises the events of the timestep that just ran and starts collecting the next one.</summary>
            public void Flush()
            {
                var events = new List<(ContactPhase Phase, ContactRecord Contact)>();

                foreach (var (key, contact) in _current)
                    events.Add((_previous.ContainsKey(key) ? ContactPhase.Stay : ContactPhase.Enter, contact));

                foreach (var (key, contact) in _previous)
                {
                    if (_current.ContainsKey(key))
                        continue;

                    if (IsStillResting(contact))
                    {
                        _current.Add(key, contact);
                        events.Add((ContactPhase.Stay, contact));
                    }
                    else
                    {
                        events.Add((ContactPhase.Exit, contact));
                    }
                }

                (_previous, _current) = (_current, _previous);
                _current.Clear();

                foreach (var (phase, contact) in events)
                    Raise(phase, contact);
            }

            private static BepuCollider3D? ColliderOf(Body3DAdapterBase? body, int childIndex)
            {
                if (body is null || body.ShapeColliders.IsEmpty)
                    return null;

                // A heightfield body's only collider is a mesh whose children are its triangles.
                if (body.ShapeColliders.Length == 1)
                    return body.ShapeColliders[0];

                return body.ShapeColliders[childIndex];
            }

            private static bool TryDeepestContact(ref ConvexContactManifold manifold, out Vector3 offset, out float depth)
            {
                offset = default;
                depth = float.NegativeInfinity;

                for (int i = 0; i < manifold.Count; i++)
                {
                    ref var contact = ref Unsafe.Add(ref manifold.Contact0, i);
                    if (contact.Depth > depth)
                    {
                        depth = contact.Depth;
                        offset = contact.Offset;
                    }
                }

                // Negative depth is a speculative contact: the colliders are close but not touching.
                return depth >= 0f;
            }

            private void Record(ContactRecord contact)
            {
                var canonical = string.CompareOrdinal(contact.ColliderA.Id, contact.ColliderB.Id) <= 0
                    ? contact
                    : contact with
                    {
                        ColliderA = contact.ColliderB,
                        ColliderB = contact.ColliderA,
                        BodyA = contact.BodyB,
                        BodyB = contact.BodyA,
                        Normal = -contact.Normal,
                    };

                var key = canonical.Key;
                if (!_current.TryGetValue(key, out var existing) || canonical.Depth > existing.Depth)
                    _current[key] = canonical;
            }

            /// <summary>
            /// Sleeping bodies skip the narrow phase, so a pair whose non-static bodies are all asleep is still touching
            /// as long as both colliders are still attached to live bodies.
            /// </summary>
            private bool IsStillResting(ContactRecord contact)
                => IsAsleepWith(contact.BodyA, contact.ColliderA)
                   && IsAsleepWith(contact.BodyB, contact.ColliderB)
                   && (contact.BodyA is DynamicBody3DAdapter || contact.BodyB is DynamicBody3DAdapter);

            private bool IsAsleepWith(Body3DAdapterBase body, BepuCollider3D collider)
            {
                if (body.IsRemoved || !body.ShapeColliders.Contains(collider))
                    return false;

                return body is not DynamicBody3DAdapter dynamic
                       || !_engine.Simulation.Bodies.GetBodyReference(dynamic.Handle).Awake;
            }

            private static void Raise(ContactPhase phase, ContactRecord contact)
            {
                if (contact.IsTrigger)
                {
                    RaiseTrigger(phase, contact);
                    return;
                }

                // Normal points from B to A, so it points out of B and into A.
                var forA = new PhysxCollisionInfo3D(
                    contact.ColliderA, contact.ColliderB, contact.BodyA, contact.BodyB, contact.Point, -contact.Normal, 0f);
                var forB = new PhysxCollisionInfo3D(
                    contact.ColliderB, contact.ColliderA, contact.BodyB, contact.BodyA, contact.Point, contact.Normal, 0f);

                switch (phase)
                {
                    case ContactPhase.Enter:
                        contact.ColliderA.RaiseCollisionEnter(forA);
                        contact.ColliderB.RaiseCollisionEnter(forB);
                        break;
                    case ContactPhase.Stay:
                        contact.ColliderA.RaiseCollisionStay(forA);
                        contact.ColliderB.RaiseCollisionStay(forB);
                        break;
                    case ContactPhase.Exit:
                        contact.ColliderA.RaiseCollisionExit(forA);
                        contact.ColliderB.RaiseCollisionExit(forB);
                        break;
                }
            }

            private static void RaiseTrigger(ContactPhase phase, ContactRecord contact)
            {
                switch (phase)
                {
                    case ContactPhase.Enter:
                        contact.ColliderA.RaiseTriggerEnter(contact.ColliderA, contact.ColliderB);
                        contact.ColliderB.RaiseTriggerEnter(contact.ColliderB, contact.ColliderA);
                        break;
                    case ContactPhase.Exit:
                        contact.ColliderA.RaiseTriggerExit(contact.ColliderA, contact.ColliderB);
                        contact.ColliderB.RaiseTriggerExit(contact.ColliderB, contact.ColliderA);
                        break;
                }
            }
        }

        private readonly struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
        {
            private readonly ContactEventTracker _contacts;

            public NarrowPhaseCallbacks(ContactEventTracker contacts) => _contacts = contacts;

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

            // Every collider lives in a compound (or is a heightfield mesh), so collider pairs always arrive here.
            public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
            {
                _contacts.OnChildManifold(pair, childIndexA, childIndexB, ref manifold);
                return true;
            }

            public void Dispose() { }
        }

        private readonly struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
        {
            private readonly Vector3 _gravity;

            public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
            public bool AllowSubstepsForUnconstrainedBodies => false;
            public bool IntegrateVelocityForKinematics => false;

            public PoseIntegratorCallbacks(Vector3 gravity) => _gravity = gravity;

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
                var g = Vector3Wide.Broadcast(_gravity);
                Vector3Wide.Scale(g, dt, out var gdt);
                velocity.Linear += gdt;
            }
        }
    }
}
