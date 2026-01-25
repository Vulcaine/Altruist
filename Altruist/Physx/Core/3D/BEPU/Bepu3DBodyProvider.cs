/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Runtime.InteropServices;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU-backed body API provider (3D).
    /// </summary>
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        // Dynamic body original shapes (restore on detach)
        private readonly Dictionary<BodyHandle, TypedIndex> _originalShapeByBody = new();

        // Static body original shapes (restore on detach)
        private readonly Dictionary<StaticHandle, TypedIndex> _originalShapeByStatic = new();

        private readonly struct Attachment
        {
            public readonly bool IsStatic;
            public readonly BodyHandle Body;
            public readonly StaticHandle Static;
            public readonly TypedIndex Shape;

            public Attachment(BodyHandle body, TypedIndex shape)
            {
                IsStatic = false;
                Body = body;
                Static = default;
                Shape = shape;
            }

            public Attachment(StaticHandle stat, TypedIndex shape)
            {
                IsStatic = true;
                Body = default;
                Static = stat;
                Shape = shape;
            }
        }

        private readonly Dictionary<IPhysxCollider3D, Attachment> _attachments = new();

        public BepuPhysxBodyApiProvider3D() { }

        /// <summary>
        /// Create a BEPU-backed IPhysxBody3D for the given engine from a body descriptor.
        /// </summary>
        public IPhysxBody3D CreateBody(IPhysxWorldEngine3D engine, in PhysxBody3DDesc desc)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            var transform = desc.Transform;
            var pos = transform.Position.ToVector3();
            var ori = transform.Rotation.ToQuaternion();
            var halfExtents = transform.Size.ToVector3();

            // Default placeholder box shape (gets replaced by colliders you attach)
            var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
            var shapeIndex = engine3D.Simulation.Shapes.Add(box);

            // ✅ REAL STATIC -> Simulation.Statics
            if (desc.Type == PhysxBodyType.Static)
            {
                var staticDesc = new StaticDescription(
                    new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                    ori,
                    shapeIndex
                );

                var staticHandle = engine3D.Simulation.Statics.Add(staticDesc);
                return new Static3DAdapter(desc.Id, engine3D, staticHandle);
            }

            // Dynamic / Kinematic -> Simulation.Bodies
            var pose = new RigidPose(
                new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                ori
            );

            BodyInertia inertia = default;
            if (desc.Type == PhysxBodyType.Dynamic)
            {
                var useMass = desc.Mass > 0f ? desc.Mass : 1f;
                inertia = box.ComputeInertia(useMass);
            }

            var collidable = new CollidableDescription(shapeIndex, 0.1f);
            var activity = new BodyActivityDescription(0.01f);

            BodyDescription bodyDesc = desc.Type switch
            {
                PhysxBodyType.Dynamic => BodyDescription.CreateDynamic(pose, inertia, collidable, activity),
                PhysxBodyType.Kinematic => BodyDescription.CreateKinematic(pose, collidable, activity),
                _ => BodyDescription.CreateKinematic(pose, collidable, activity)
            };

            if (desc.Type != PhysxBodyType.Dynamic)
                bodyDesc.LocalInertia = default;

            var handle = engine3D.Simulation.Bodies.Add(bodyDesc);

            return new BepuWorldEngine3D.Body3DAdapter(
                desc.Id,
                engine3D,
                handle,
                desc.Type,
                desc.Mass > 0f ? desc.Mass : 0f
            );
        }

        /// <summary>
        /// Attach a collider to a body.
        /// For dynamics: swap Collidable.Shape.
        /// For statics: rebuild the static because StaticReference.Shape is read-only in your BEPU version.
        /// </summary>
        public void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider)
        {
            if (_attachments.ContainsKey(collider))
                throw new InvalidOperationException("This collider is already attached.");

            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            // ✅ STATIC handling (rebuild static)
            if (body is Static3DAdapter staticOwner)
            {
                var statics = engine3D.Simulation.Statics;

                var staticRef = statics.GetStaticReference(staticOwner.Handle);

                // Save original shape the first time
                if (!_originalShapeByStatic.ContainsKey(staticOwner.Handle))
                    _originalShapeByStatic[staticOwner.Handle] = staticRef.Shape;

                // New shape
                var newShape = CreateBepuShapeIndexFromCollider(engine3D, collider);

                // Rebuild static with same pose but different shape
                var newHandle = RebuildStatic(
                    engine3D,
                    oldHandle: staticOwner.Handle,
                    pose: staticRef.Pose,
                    newShape: newShape
                );

                // Update adapter + bookkeeping
                staticOwner.ReplaceHandle(newHandle);

                _attachments[collider] = new Attachment(newHandle, newShape);
                staticOwner.AddCollider(collider);
                return;
            }

            // ✅ DYNAMIC / KINEMATIC handling (swap body collidable shape)
            if (body is not BepuWorldEngine3D.Body3DAdapter owner)
                throw new InvalidOperationException("Body must be a BEPU Body3DAdapter or Static3DAdapter.");

            var bodies = engine3D.Simulation.Bodies;
            var bodyRef = bodies.GetBodyReference(owner.Handle);

            if (!_originalShapeByBody.ContainsKey(owner.Handle))
                _originalShapeByBody[owner.Handle] = bodyRef.Collidable.Shape;

            var newShapeIndex = CreateBepuShapeIndexFromCollider(engine3D, collider);
            bodyRef.Collidable.Shape = newShapeIndex;

            _attachments[collider] = new Attachment(owner.Handle, newShapeIndex);
            owner.AddCollider(collider);
        }

        /// <summary>
        /// Remove collider and restore original shape.
        /// </summary>
        public void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            if (!_attachments.TryGetValue(collider, out var rec))
                return;

            if (rec.IsStatic)
            {
                var statics = engine3D.Simulation.Statics;
                var staticRef = statics.GetStaticReference(rec.Static);

                if (_originalShapeByStatic.TryGetValue(rec.Static, out var original))
                {
                    // Rebuild static back to original shape
                    var newHandle = RebuildStatic(
                        engine3D,
                        oldHandle: rec.Static,
                        pose: staticRef.Pose,
                        newShape: original
                    );

                    // Update stored original-shape dictionary to follow new handle
                    _originalShapeByStatic.Remove(rec.Static);
                    _originalShapeByStatic[newHandle] = original;

                    // Also update attachment record to new handle (if needed)
                    _attachments.Remove(collider);
                    return;
                }

                _attachments.Remove(collider);
                return;
            }

            // Dynamic restore
            {
                var bodies = engine3D.Simulation.Bodies;
                var bodyRef = bodies.GetBodyReference(rec.Body);

                if (_originalShapeByBody.TryGetValue(rec.Body, out var original))
                    bodyRef.Collidable.Shape = original;

                _attachments.Remove(collider);
            }
        }

        private static StaticHandle RebuildStatic(
            BepuWorldEngine3D engine,
            StaticHandle oldHandle,
            in RigidPose pose,
            TypedIndex newShape)
        {
            // Remove old
            engine.Simulation.Statics.Remove(oldHandle);

            // Add new
            var newDesc = new StaticDescription(pose.Position, pose.Orientation, newShape);
            return engine.Simulation.Statics.Add(newDesc);
        }

        // -------------------- helpers --------------------

        private TypedIndex CreateBepuShapeIndexFromCollider(BepuWorldEngine3D engine, IPhysxCollider3D c)
        {
            var t = c.Transform;

            switch (c.Shape)
            {
                case PhysxColliderShape3D.Sphere3D:
                    {
                        var radius = t.Size.X;
                        return engine.Simulation.Shapes.Add(new Sphere(radius));
                    }

                case PhysxColliderShape3D.Box3D:
                    {
                        var fullX = t.Size.X * 2f;
                        var fullY = t.Size.Y * 2f;
                        var fullZ = t.Size.Z * 2f;
                        return engine.Simulation.Shapes.Add(new Box(fullX, fullY, fullZ));
                    }

                case PhysxColliderShape3D.Capsule3D:
                    {
                        var radius = t.Size.X;
                        var length = t.Size.Y * 2f;
                        return engine.Simulation.Shapes.Add(new Capsule(radius, length));
                    }

                case PhysxColliderShape3D.Heightfield3D:
                    {
                        if (c.Heightfield is { } hf)
                        {
                            var mesh = BuildMeshFromHeightfield(hf, engine.Simulation.BufferPool);
                            return engine.Simulation.Shapes.Add(mesh);
                        }

                        throw new InvalidOperationException("Heightmap collider has no HeightfieldData.");
                    }

                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }

        private static Mesh BuildMeshFromHeightfield(HeightfieldData hf, BufferPool pool)
        {
            int width = hf.Width;
            int length = hf.Height;

            float cellSizeX = hf.CellSizeX;
            float cellSizeZ = hf.CellSizeZ;

            int quadCount = (width - 1) * (length - 1);
            int triangleCount = quadCount * 2;

            pool.Take(triangleCount, out Buffer<Triangle> triangles);

            int triIndex = 0;

            for (int z = 0; z < length - 1; z++)
            {
                for (int x = 0; x < width - 1; x++)
                {
                    float h00 = hf.Heights[x, z];
                    float h10 = hf.Heights[x + 1, z];
                    float h01 = hf.Heights[x, z + 1];
                    float h11 = hf.Heights[x + 1, z + 1];

                    var v00 = new Vector3(x * cellSizeX, h00, z * cellSizeZ);
                    var v10 = new Vector3((x + 1) * cellSizeX, h10, z * cellSizeZ);
                    var v01 = new Vector3(x * cellSizeX, h01, (z + 1) * cellSizeZ);
                    var v11 = new Vector3((x + 1) * cellSizeX, h11, (z + 1) * cellSizeZ);

                    ref var t0 = ref triangles[triIndex++];
                    t0.A = v00;
                    t0.B = v01;
                    t0.C = v10;

                    ref var t1 = ref triangles[triIndex++];
                    t1.A = v10;
                    t1.B = v01;
                    t1.C = v11;
                }
            }

            return new Mesh(triangles, new Vector3(1f, 1f, 1f), pool);
        }
    }

    /// <summary>
    /// Adapter that represents a BEPU Static in your IPhysxBody3D abstraction.
    /// </summary>
    internal sealed class Static3DAdapter : IPhysxBody3D
    {
        public string Id { get; }
        public PhysxBodyType Type { get; set; }
        public float Mass { get => 0f; set { } }
        public object? UserData { get; set; }

        public StaticHandle Handle => _handle;

        private readonly BepuWorldEngine3D _engine;
        private StaticHandle _handle;
        private readonly List<IPhysxCollider> _colliders = new();

        public Static3DAdapter(string id, BepuWorldEngine3D engine, StaticHandle handle)
        {
            Id = id;
            _engine = engine;
            _handle = handle;
            Type = PhysxBodyType.Static;
        }

        public void ReplaceHandle(StaticHandle newHandle) => _handle = newHandle;

        public Vector3 Position
        {
            get
            {
                var s = _engine.Simulation.Statics.GetStaticReference(_handle);
                return s.Pose.Position;
            }
            set
            {
                // Statics aren't meant to move often; if you really want moving terrain,
                // you should rebuild the static. But we allow pose mutation anyway.
                var s = _engine.Simulation.Statics.GetStaticReference(_handle);
                s.Pose.Position = value;
            }
        }

        public Quaternion Rotation
        {
            get
            {
                var s = _engine.Simulation.Statics.GetStaticReference(_handle);
                return s.Pose.Orientation;
            }
            set
            {
                var s = _engine.Simulation.Statics.GetStaticReference(_handle);
                s.Pose.Orientation = value;
            }
        }

        public Vector3 LinearVelocity
        {
            get => Vector3.Zero;
            set { /* statics don't move */ }
        }

        public Vector3 AngularVelocity
        {
            get => Vector3.Zero;
            set { /* statics don't rotate via velocity */ }
        }

        public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);
        public bool RemoveCollider(IPhysxCollider collider) => _colliders.Remove(collider);
        public ReadOnlySpan<IPhysxCollider> GetColliders() => CollectionsMarshal.AsSpan(_colliders);

        // ✅ required by your interface
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

        // ✅ required by your interface
        public IPhysxCollider? GetColliderAt(int index)
        {
            if ((uint)index < (uint)_colliders.Count)
                return _colliders[index];
            return null;
        }

        public void ApplyForce(in PhysxForce force)
        {
            // Static bodies ignore forces.
        }
    }
}
