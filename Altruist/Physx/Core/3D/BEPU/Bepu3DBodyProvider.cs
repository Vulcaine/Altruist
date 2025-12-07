/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU-backed body API provider (3D).
    ///
    /// Uses engine-agnostic descriptors:
    ///   - PhysxBody3DDesc for bodies
    ///   - PhysxCollider3DDesc for colliders
    ///
    /// and interprets them to create BEPU bodies/collidables for a specific engine.
    /// </summary>
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        // Store the original body shape so we can restore it after removing an attached collider.
        private readonly Dictionary<BodyHandle, TypedIndex> _originalShapeByBody = new();

        // Track which collider is attached to which body and what shape it installed.
        private readonly Dictionary<IPhysxCollider3D, (BodyHandle Body, TypedIndex Shape)> _attachments = new();

        public BepuPhysxBodyApiProvider3D()
        {
        }

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

            // Default box using transform size as half extents (so full size is doubled)
            var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
            var shapeIndex = engine3D.Simulation.Shapes.Add(box);

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
            {
                bodyDesc.LocalInertia = default;
            }

            var handle = engine3D.Simulation.Bodies.Add(bodyDesc);

            // Adapter needs an id and knowledge about its engine/handle.
            var adapter = new BepuWorldEngine3D.Body3DAdapter(
                desc.Id,
                engine3D,
                handle,
                desc.Type,
                desc.Mass > 0f ? desc.Mass : 0f
            );

            return adapter;
        }

        /// <summary>
        /// Attach a collider to a body by swapping the body's current shape for the collider's shape.
        /// Supports one collider via this API at a time per body (simple swap/restore model).
        /// </summary>
        public void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider)
        {
            if (body is not BepuWorldEngine3D.Body3DAdapter owner)
                throw new InvalidOperationException("Body must be a BEPU-backed Body3DAdapter.");

            if (_attachments.ContainsKey(collider))
                throw new InvalidOperationException("This collider is already attached.");

            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            var bodies = engine3D.Simulation.Bodies;
            var bodyRef = bodies.GetBodyReference(owner.Handle);

            // Remember original shape (first time we attach to this body)
            if (!_originalShapeByBody.ContainsKey(owner.Handle))
            {
                _originalShapeByBody[owner.Handle] = bodyRef.Collidable.Shape;
            }

            // Build a BEPU shape from collider data and add it to the shape registry
            var shapeIndex = CreateBepuShapeIndexFromCollider(engine3D, collider);

            bodyRef.Collidable.Shape = shapeIndex;

            _attachments[collider] = (owner.Handle, shapeIndex);
        }

        /// <summary>
        /// Detach the collider: if it is currently attached, restore the body's original shape.
        /// </summary>
        public void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            if (!_attachments.TryGetValue(collider, out var rec))
                return;

            var handle = rec.Body;
            var bodies = engine3D.Simulation.Bodies;
            var bodyRef = bodies.GetBodyReference(handle);

            if (_originalShapeByBody.TryGetValue(handle, out var original))
            {
                bodyRef.Collidable.Shape = original;
            }

            _attachments.Remove(collider);
            // Optionally: _originalShapeByBody.Remove(handle);
        }

        // -------------------- helpers --------------------

        private TypedIndex CreateBepuShapeIndexFromCollider(BepuWorldEngine3D engine, IPhysxCollider3D c)
        {
            var t = c.Transform;

            switch (c.Shape)
            {
                case PhysxColliderShape3D.Sphere3D:
                    {
                        // Convention: Transform.Size.X stores radius
                        var radius = t.Size.X;
                        var sphere = new Sphere(radius);
                        return engine.Simulation.Shapes.Add(sphere);
                    }

                case PhysxColliderShape3D.Box3D:
                    {
                        // Convention: Transform.Size stores half extents; BEPU needs full extents.
                        var fullX = t.Size.X * 2f;
                        var fullY = t.Size.Y * 2f;
                        var fullZ = t.Size.Z * 2f;
                        var box = new Box(fullX, fullY, fullZ);
                        return engine.Simulation.Shapes.Add(box);
                    }

                case PhysxColliderShape3D.Capsule3D:
                    {
                        // Convention: Transform.Size.X = radius, Transform.Size.Y = half length -> length = 2 * halfLen
                        var radius = t.Size.X;
                        var length = t.Size.Y * 2f;
                        var capsule = new Capsule(radius, length);
                        return engine.Simulation.Shapes.Add(capsule);
                    }

                case PhysxColliderShape3D.Heightfield3D:
                    {
                        if (c.Heightfield is { } hf)
                        {
                            var mesh = BuildMeshFromHeightfield(hf, engine.Simulation.BufferPool);
                            return engine.Simulation.Shapes.Add(mesh);
                        }

                        throw new InvalidOperationException(
                            "Heightmap collider has no HeightfieldData.");
                    }
                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }

        private static Mesh BuildMeshFromHeightfield(HeightfieldData hf, BufferPool pool)
        {
            int width = hf.Width;   // X dimension (samples along X)
            int length = hf.Height;  // Z dimension (samples along Z)

            float cellSizeX = hf.CellSizeX;
            float cellSizeZ = hf.CellSizeZ;
            float heightScale = hf.HeightScale;

            // Each quad (x,z) -> (x+1,z+1) becomes 2 triangles.
            int quadCount = (width - 1) * (length - 1);
            int triangleCount = quadCount * 2;

            pool.Take(triangleCount, out Buffer<Triangle> triangles);

            int triIndex = 0;

            for (int z = 0; z < length - 1; z++)
            {
                for (int x = 0; x < width - 1; x++)
                {
                    // Sample heights (x,z), (x+1,z), (x,z+1), (x+1,z+1)
                    float h00 = hf.Heights[x, z] * heightScale;
                    float h10 = hf.Heights[x + 1, z] * heightScale;
                    float h01 = hf.Heights[x, z + 1] * heightScale;
                    float h11 = hf.Heights[x + 1, z + 1] * heightScale;

                    var v00 = new Vector3(x * cellSizeX, h00, z * cellSizeZ);
                    var v10 = new Vector3((x + 1) * cellSizeX, h10, z * cellSizeZ);
                    var v01 = new Vector3(x * cellSizeX, h01, (z + 1) * cellSizeZ);
                    var v11 = new Vector3((x + 1) * cellSizeX, h11, (z + 1) * cellSizeZ);

                    // First triangle: v00, v01, v10
                    ref var t0 = ref triangles[triIndex++];
                    t0.A = v00;
                    t0.B = v01;
                    t0.C = v10;

                    // Second triangle: v10, v01, v11
                    ref var t1 = ref triangles[triIndex++];
                    t1.A = v10;
                    t1.B = v01;
                    t1.C = v11;
                }
            }

            var mesh = new Mesh(triangles, new Vector3(1f, 1f, 1f), pool);
            return mesh;
        }
    }
}
