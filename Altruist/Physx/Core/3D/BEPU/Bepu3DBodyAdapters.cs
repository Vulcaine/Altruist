/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

namespace Altruist.Physx.ThreeD
{
    public sealed partial class BepuWorldEngine3D
    {
        /// <summary>
        /// Base class of the BEPU body adapters (<see cref="DynamicBody3DAdapter"/>, <see cref="StaticBody3DAdapter"/>).
        /// Instances are created only by <see cref="BepuPhysxBodyApiProvider3D.CreateBody"/>.
        /// </summary>
        /// <remarks>
        /// The attached colliders form the body's BEPU shape (see <see cref="BepuWorldEngine3D"/>): attaching or removing a
        /// collider rebuilds it, and removing the last collider restores the descriptor's default box. Only colliders
        /// created by <see cref="BepuPhysxColliderApiProvider3D"/> can be attached. Collider changes and pose/velocity
        /// accessors take the engine lock and throw once the body was removed or the engine disposed.
        /// </remarks>
        public abstract class Body3DAdapterBase : IPhysxBody3D
        {
            /// <inheritdoc/>
            public string Id { get; }
            /// <inheritdoc/>
            public abstract PhysxBodyType Type { get; set; }
            /// <inheritdoc/>
            public abstract float Mass { get; set; }
            /// <inheritdoc/>
            public PhysxTag? PhysxTag { get; set; }

            internal BepuWorldEngine3D Engine { get; }

            internal TypedIndex Shape { get; private set; }

            /// <summary>Colliders in compound-child order (child i of the body's shape is collider i).</summary>
            internal ImmutableArray<BepuCollider3D> ShapeColliders { get; private set; } = ImmutableArray<BepuCollider3D>.Empty;

            internal bool IsRemoved => _removed;

            private readonly Vector3 _defaultHalfExtents;

            private readonly List<IPhysxCollider> _colliders = new();

            private volatile bool _removed;

            private protected Body3DAdapterBase(string id, BepuWorldEngine3D engine, TypedIndex shape, Vector3 defaultHalfExtents)
            {
                Id = id;
                Engine = engine;
                Shape = shape;
                _defaultHalfExtents = defaultHalfExtents;
            }

            internal void MarkRemoved() => _removed = true;

            /// <summary>Throws when the engine is disposed or this body was removed from the simulation.</summary>
            /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
            /// <exception cref="InvalidOperationException">The body was removed.</exception>
            private protected void ThrowIfRemovedOrDisposed()
            {
                Engine.ThrowIfDisposed();
                if (_removed)
                    throw new InvalidOperationException($"Body '{Id}' has been removed from the simulation.");
            }

            /// <summary>Attaches <paramref name="collider"/> (ignored if already attached) and rebuilds the body's shape.</summary>
            /// <param name="collider">Collider created by <see cref="BepuPhysxColliderApiProvider3D"/>.</param>
            /// <exception cref="ArgumentNullException"><paramref name="collider"/> is <see langword="null"/>.</exception>
            /// <exception cref="ArgumentException"><paramref name="collider"/> was not created by the BEPU collider provider.</exception>
            /// <exception cref="ArgumentOutOfRangeException">The collider's size is not positive and finite.</exception>
            /// <exception cref="NotSupportedException">
            /// A heightfield collider would share the body with other colliders, or would sit on a dynamic body.
            /// </exception>
            public void AddCollider(IPhysxCollider collider)
            {
                ArgumentNullException.ThrowIfNull(collider);

                if (collider is not BepuCollider3D bepuCollider)
                    throw new ArgumentException(
                        "BEPU bodies only accept colliders created by BepuPhysxColliderApiProvider3D.", nameof(collider));

                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    if (_colliders.Contains(bepuCollider))
                        return;

                    ReplaceShape(ShapeColliders.Add(bepuCollider), Type, Mass);
                    _colliders.Add(bepuCollider);
                }
            }

            /// <summary>Detaches <paramref name="collider"/> and rebuilds the body's shape from the remaining colliders.</summary>
            /// <param name="collider">Collider to detach.</param>
            /// <returns><see langword="true"/> if it was attached.</returns>
            public bool RemoveCollider(IPhysxCollider collider)
            {
                if (collider is not BepuCollider3D bepuCollider)
                    return false;

                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    if (!_colliders.Contains(bepuCollider))
                        return false;

                    ReplaceShape(ShapeColliders.Remove(bepuCollider), Type, Mass);
                    _colliders.Remove(bepuCollider);
                    return true;
                }
            }

            /// <summary>
            /// Builds the shape for <paramref name="colliders"/> as a body of <paramref name="type"/> and
            /// <paramref name="mass"/>, swaps it in and releases the old one. Throws before changing anything when the
            /// combination is invalid.
            /// </summary>
            private protected void ReplaceShape(ImmutableArray<BepuCollider3D> colliders, PhysxBodyType type, float mass)
            {
                var built = Engine.BuildShape(colliders, type, mass, _defaultHalfExtents);
                ApplyShape(built);
                Engine.DisposeShape(Shape);
                Shape = built.Index;
                ShapeColliders = colliders;
            }

            /// <summary>Makes <paramref name="shape"/> the body's simulation shape (and inertia, for bodies in the Bodies set).</summary>
            private protected abstract void ApplyShape(BodyShape shape);

            /// <inheritdoc/>
            public ReadOnlySpan<IPhysxCollider> GetColliders() => CollectionsMarshal.AsSpan(_colliders);

            /// <inheritdoc/>
            public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
            {
                if (!string.IsNullOrEmpty(colliderId))
                {
                    foreach (var c in _colliders)
                    {
                        if (string.Equals(c.Id, colliderId, StringComparison.Ordinal))
                        {
                            collider = c;
                            return true;
                        }
                    }
                }

                collider = default!;
                return false;
            }

            /// <inheritdoc/>
            public IPhysxCollider? GetColliderAt(int index)
                => (uint)index < (uint)_colliders.Count ? _colliders[index] : null;

            /// <inheritdoc/>
            public abstract Vector3 Position { get; set; }
            /// <inheritdoc/>
            public abstract Quaternion Rotation { get; set; }
            /// <inheritdoc/>
            public abstract Vector3 LinearVelocity { get; set; }
            /// <inheritdoc/>
            public abstract Vector3 AngularVelocity { get; set; }
            /// <inheritdoc/>
            public abstract void ApplyForce(in PhysxForce force);
        }

        /// <summary>
        /// Adapter for a BEPU body in the <c>Bodies</c> set; used for both dynamic and kinematic bodies.
        /// Every setter and force command takes the engine lock and wakes the body.
        /// </summary>
        public sealed class DynamicBody3DAdapter : Body3DAdapterBase
        {
            /// <summary>Underlying BEPU body handle.</summary>
            public BodyHandle Handle { get; }

            private PhysxBodyType _type;
            private float _mass;

            internal DynamicBody3DAdapter(
                string id,
                BepuWorldEngine3D engine,
                BodyHandle handle,
                PhysxBodyType type,
                float mass,
                TypedIndex shape,
                Vector3 defaultHalfExtents)
                : base(id, engine, shape, defaultHalfExtents)
            {
                Handle = handle;
                _type = type;
                _mass = mass;
            }

            /// <summary>
            /// <see cref="PhysxBodyType.Dynamic"/> or <see cref="PhysxBodyType.Kinematic"/>. Setting it switches the BEPU
            /// body between dynamic (finite inertia from <see cref="Mass"/> and the shape) and kinematic (infinite inertia).
            /// </summary>
            /// <exception cref="InvalidOperationException">Set to <see cref="PhysxBodyType.Static"/>: create a static body instead.</exception>
            /// <exception cref="NotSupportedException">Set to dynamic while the body has a heightfield collider.</exception>
            public override PhysxBodyType Type
            {
                get => _type;
                set
                {
                    if (value == PhysxBodyType.Static)
                        throw new InvalidOperationException($"Body '{Id}' cannot become static; create a static body instead.");

                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        if (value == _type)
                            return;

                        ReplaceShape(ShapeColliders, value, _mass);
                        _type = value;
                    }
                }
            }

            /// <summary>
            /// Mass of a dynamic body (0 for kinematic bodies, which have infinite mass). Setting it recomputes the BEPU
            /// inertia; the mass is spread over the colliders by volume.
            /// </summary>
            /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
            /// <exception cref="InvalidOperationException">The body is kinematic: switch <see cref="Type"/> to dynamic first.</exception>
            public override float Mass
            {
                get => _type == PhysxBodyType.Kinematic ? 0f : _mass;
                set
                {
                    if (!float.IsFinite(value) || value <= 0f)
                        throw new ArgumentOutOfRangeException(nameof(value), value, "Mass must be positive and finite.");

                    lock (Engine._sync)
                    {
                        ThrowIfRemovedOrDisposed();
                        if (_type == PhysxBodyType.Kinematic)
                            throw new InvalidOperationException($"Kinematic body '{Id}' has infinite mass; make it dynamic first.");

                        ReplaceShape(ShapeColliders, _type, value);
                        _mass = value;
                    }
                }
            }

            private protected override void ApplyShape(BodyShape shape)
            {
                var bodies = Engine.Simulation.Bodies;
                bodies.SetShape(Handle, shape.Index);
                bodies.SetLocalInertia(Handle, shape.Inertia);
            }

            /// <inheritdoc/>
            public override Vector3 Position
            {
                get => Read(static b => b.Pose.Position);
                set => Write(value, static (b, v) => b.Pose.Position = v);
            }

            /// <inheritdoc/>
            public override Quaternion Rotation
            {
                get => Read(static b => b.Pose.Orientation);
                set => Write(value, static (b, v) => b.Pose.Orientation = v);
            }

            /// <inheritdoc/>
            public override Vector3 LinearVelocity
            {
                get => Read(static b => b.Velocity.Linear);
                set => Write(value, static (b, v) => b.Velocity.Linear = v);
            }

            /// <inheritdoc/>
            public override Vector3 AngularVelocity
            {
                get => Read(static b => b.Velocity.Angular);
                set => Write(value, static (b, v) => b.Velocity.Angular = v);
            }

            private T Read<T>(Func<BodyReference, T> read)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();
                    return read(Engine.Simulation.Bodies.GetBodyReference(Handle));
                }
            }

            private void Write<T>(T value, Action<BodyReference, T> write)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();
                    write(Engine.Simulation.Bodies.GetBodyReference(Handle), value);
                    Engine.Simulation.Awakener.AwakenBody(Handle);
                }
            }

            /// <summary>
            /// Executes a 3D force command: <c>AddForce3D</c> and <c>AddTorque3D</c> act continuously over every timestep
            /// of the next <see cref="Step"/> that runs one (impulse <c>value * FixedDeltaTime</c> per timestep), then are
            /// cleared; <c>AddImpulse3D</c> is an immediate linear impulse; the set-velocity kinds overwrite velocity.
            /// Kinematic bodies have infinite mass, so forces, torques and impulses do not move them. 2D kinds are
            /// ignored. Always wakes the body.
            /// </summary>
            /// <param name="force">Force command.</param>
            public override void ApplyForce(in PhysxForce force)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();

                    var body = Engine.Simulation.Bodies.GetBodyReference(Handle);

                    switch (force.Type)
                    {
                        case PhysxForce.Kind.AddForce3D:
                            Engine.AddContinuousLoad_NoLock(Handle, force.Vector, Vector3.Zero);
                            break;
                        case PhysxForce.Kind.AddTorque3D:
                            Engine.AddContinuousLoad_NoLock(Handle, Vector3.Zero, force.Vector);
                            break;
                        case PhysxForce.Kind.AddImpulse3D:
                            body.ApplyLinearImpulse(force.Vector);
                            break;
                        case PhysxForce.Kind.SetLinearVelocity3D:
                            body.Velocity.Linear = force.Vector;
                            break;
                        case PhysxForce.Kind.SetAngularVelocity3D:
                            body.Velocity.Angular = force.Vector;
                            break;
                        default:
                            return;
                    }

                    Engine.Simulation.Awakener.AwakenBody(Handle);
                }
            }
        }

        /// <summary>
        /// Adapter for a BEPU static. Type is always <see cref="PhysxBodyType.Static"/>, mass 0, velocities zero, and
        /// <see cref="ApplyForce"/> does nothing. Moving it updates its broad-phase bounds and wakes sleeping bodies around
        /// its old and new place.
        /// </summary>
        public sealed class StaticBody3DAdapter : Body3DAdapterBase
        {
            /// <summary>BEPU static handle; stable for the body's lifetime.</summary>
            public StaticHandle Handle { get; }

            internal StaticBody3DAdapter(
                string id,
                BepuWorldEngine3D engine,
                StaticHandle handle,
                TypedIndex shape,
                Vector3 defaultHalfExtents)
                : base(id, engine, shape, defaultHalfExtents)
            {
                Handle = handle;
            }

            /// <summary>Always <see cref="PhysxBodyType.Static"/>.</summary>
            /// <exception cref="InvalidOperationException">Set to another type: create a dynamic or kinematic body instead.</exception>
            public override PhysxBodyType Type
            {
                get => PhysxBodyType.Static;
                set
                {
                    if (value != PhysxBodyType.Static)
                        throw new InvalidOperationException($"Static body '{Id}' cannot change type; create a {value} body instead.");
                }
            }

            /// <summary>Always 0 (statics have infinite mass).</summary>
            /// <exception cref="InvalidOperationException">Set to a non-zero mass.</exception>
            public override float Mass
            {
                get => 0f;
                set
                {
                    if (value != 0f)
                        throw new InvalidOperationException($"Static body '{Id}' has infinite mass and cannot be given a mass.");
                }
            }

            private protected override void ApplyShape(BodyShape shape)
            {
                var pose = Engine.Simulation.Statics.GetStaticReference(Handle).Pose;
                Engine.Simulation.Statics.ApplyDescription(Handle, new StaticDescription(pose.Position, pose.Orientation, shape.Index));
            }

            /// <inheritdoc/>
            public override Vector3 Position
            {
                get => ReadPose().Position;
                set => MovePose(pose => new RigidPose(value, pose.Orientation));
            }

            /// <inheritdoc/>
            public override Quaternion Rotation
            {
                get => ReadPose().Orientation;
                set => MovePose(pose => new RigidPose(pose.Position, value));
            }

            private RigidPose ReadPose()
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();
                    return Engine.Simulation.Statics.GetStaticReference(Handle).Pose;
                }
            }

            private void MovePose(Func<RigidPose, RigidPose> move)
            {
                lock (Engine._sync)
                {
                    ThrowIfRemovedOrDisposed();
                    var pose = move(Engine.Simulation.Statics.GetStaticReference(Handle).Pose);
                    Engine.Simulation.Statics.ApplyDescription(Handle, new StaticDescription(pose.Position, pose.Orientation, Shape));
                }
            }

            /// <summary>Always zero; setting it does nothing (statics never move on their own).</summary>
            public override Vector3 LinearVelocity
            {
                get => Vector3.Zero;
                set { }
            }

            /// <summary>Always zero; setting it does nothing (statics never move on their own).</summary>
            public override Vector3 AngularVelocity
            {
                get => Vector3.Zero;
                set { }
            }

            /// <summary>Does nothing: static bodies ignore all force commands (see <see cref="PhysxForce"/>).</summary>
            /// <param name="force">Ignored.</param>
            public override void ApplyForce(in PhysxForce force) { }
        }
    }
}
