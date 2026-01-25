/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Reflection;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD
{
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        private readonly Dictionary<BodyHandle, TypedIndex> _originalShapeByBody = new();
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

        public IPhysxBody3D CreateBody(IPhysxWorldEngine3D engine, in PhysxBody3DDesc desc)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            lock (engine3D._sync)
            {
                var transform = desc.Transform;
                var pos = transform.Position.ToVector3();
                var ori = transform.Rotation.ToQuaternion();
                var halfExtents = transform.Size.ToVector3();

                var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
                var shapeIndex = engine3D.Simulation.Shapes.Add(box);

                if (desc.Type == PhysxBodyType.Static)
                {
                    var staticDesc = new StaticDescription(
                        new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                        ori,
                        shapeIndex
                    );

                    var staticHandle = engine3D.Simulation.Statics.Add(staticDesc);
                    return new BepuWorldEngine3D.StaticBody3DAdapter(desc.Id, engine3D, staticHandle);
                }

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

                return new BepuWorldEngine3D.DynamicBody3DAdapter(
                    desc.Id,
                    engine3D,
                    handle,
                    desc.Type,
                    desc.Mass > 0f ? desc.Mass : 0f
                );
            }
        }

        public void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider)
        {
            if (_attachments.ContainsKey(collider))
                throw new InvalidOperationException("This collider is already attached.");

            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            lock (engine3D._sync)
            {
                if (body is BepuWorldEngine3D.StaticBody3DAdapter staticOwner)
                {
                    var statics = engine3D.Simulation.Statics;
                    var staticRef = statics.GetStaticReference(staticOwner.Handle);

                    if (!_originalShapeByStatic.ContainsKey(staticOwner.Handle))
                        _originalShapeByStatic[staticOwner.Handle] = staticRef.Shape;

                    var newShape = CreateBepuShapeIndexFromCollider(engine3D, collider);
                    var newHandle = RebuildStatic(engine3D, staticOwner.Handle, staticRef.Pose, newShape);

                    SetStaticHandle(staticOwner, newHandle);

                    _attachments[collider] = new Attachment(newHandle, newShape);
                    staticOwner.AddCollider(collider);
                    return;
                }

                if (body is not BepuWorldEngine3D.DynamicBody3DAdapter owner)
                    throw new InvalidOperationException("Body must be a BEPU DynamicBody3DAdapter or StaticBody3DAdapter.");

                var bodies = engine3D.Simulation.Bodies;
                var bodyRef = bodies.GetBodyReference(owner.Handle);

                if (!_originalShapeByBody.ContainsKey(owner.Handle))
                    _originalShapeByBody[owner.Handle] = bodyRef.Collidable.Shape;

                var newShapeIndex = CreateBepuShapeIndexFromCollider(engine3D, collider);
                bodyRef.Collidable.Shape = newShapeIndex;

                _attachments[collider] = new Attachment(owner.Handle, newShapeIndex);
                owner.AddCollider(collider);
            }
        }

        public void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            lock (engine3D._sync)
            {
                if (!_attachments.TryGetValue(collider, out var rec))
                    return;

                if (rec.IsStatic)
                {
                    var statics = engine3D.Simulation.Statics;
                    var staticRef = statics.GetStaticReference(rec.Static);

                    if (_originalShapeByStatic.TryGetValue(rec.Static, out var original))
                    {
                        var newHandle = RebuildStatic(engine3D, rec.Static, staticRef.Pose, original);

                        _originalShapeByStatic.Remove(rec.Static);
                        _originalShapeByStatic[newHandle] = original;

                        _attachments.Remove(collider);
                        return;
                    }

                    _attachments.Remove(collider);
                    return;
                }

                var bodies = engine3D.Simulation.Bodies;
                var bodyRef = bodies.GetBodyReference(rec.Body);

                if (_originalShapeByBody.TryGetValue(rec.Body, out var originalBodyShape))
                    bodyRef.Collidable.Shape = originalBodyShape;

                _attachments.Remove(collider);
            }
        }

        private static StaticHandle RebuildStatic(
            BepuWorldEngine3D engine,
            StaticHandle oldHandle,
            in RigidPose pose,
            TypedIndex newShape)
        {
            engine.Simulation.Statics.Remove(oldHandle);
            var newDesc = new StaticDescription(pose.Position, pose.Orientation, newShape);
            return engine.Simulation.Statics.Add(newDesc);
        }

        private static void SetStaticHandle(BepuWorldEngine3D.StaticBody3DAdapter adapter, StaticHandle newHandle)
        {
            var t = adapter.GetType();
            var field = t.GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException("StaticBody3DAdapter does not expose a writable handle field.");
            field.SetValue(adapter, newHandle);
        }

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
}
