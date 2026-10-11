/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using System.Collections.Immutable;
using System.Numerics;

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

namespace Altruist.Physx.ThreeD
{
    public sealed partial class BepuWorldEngine3D
    {
        /// <summary>A BEPU shape built for a body, and the inertia it gives a dynamic body (default for others).</summary>
        internal readonly record struct BodyShape(TypedIndex Index, BodyInertia Inertia);

        /// <summary>
        /// Builds the shape for a body with these colliders: the default box when there are none, the heightfield mesh
        /// for a single heightfield collider, otherwise a compound with one child per collider (child i = collider i).
        /// Validates everything before adding any shape, so a failure leaves the shape set unchanged.
        /// </summary>
        internal BodyShape BuildShape(
            ImmutableArray<BepuCollider3D> colliders,
            PhysxBodyType type,
            float mass,
            Vector3 defaultHalfExtents)
        {
            if (colliders.IsEmpty)
                return BuildDefaultBox(type, mass, defaultHalfExtents);

            if (colliders.Any(c => c.Shape == PhysxColliderShape3D.Heightfield3D))
                return BuildHeightfield(colliders, type);

            foreach (var collider in colliders)
                ValidateConvex(collider);

            return BuildCompound(colliders, type, mass);
        }

        internal void DisposeShape(TypedIndex shape) => _simulation.Shapes.RecursivelyRemoveAndDispose(shape, _pool);

        // Colliders sit at the body origin: their transform's position and rotation are not applied (see the class remarks).
        private static RigidPose LocalPose(BepuCollider3D collider) => RigidPose.Identity;

        private BodyShape BuildDefaultBox(PhysxBodyType type, float mass, Vector3 halfExtents)
        {
            var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
            var inertia = type == PhysxBodyType.Dynamic ? box.ComputeInertia(mass) : default;
            return new BodyShape(_simulation.Shapes.Add(box), inertia);
        }

        private BodyShape BuildHeightfield(ImmutableArray<BepuCollider3D> colliders, PhysxBodyType type)
        {
            if (colliders.Length != 1)
                throw new NotSupportedException("A heightfield collider must be the only collider on its body.");

            if (type == PhysxBodyType.Dynamic)
                throw new NotSupportedException("A heightfield collider needs a static or kinematic body.");

            var data = colliders[0].Heightfield
                ?? throw new ArgumentException("A heightfield collider has no HeightfieldData.", nameof(colliders));

            if (data.Width < 2 || data.Height < 2)
                throw new ArgumentException("A heightfield needs at least 2×2 samples.", nameof(colliders));

            var mesh = BepuHeightfieldMesh.Create(data, _pool);
            return new BodyShape(_simulation.Shapes.Add(mesh), default);
        }

        private BodyShape BuildCompound(ImmutableArray<BepuCollider3D> colliders, PhysxBodyType type, float mass)
        {
            var volumes = colliders.Select(Volume).ToArray();
            var totalVolume = volumes.Sum();

            var builder = new CompoundBuilder(_pool, _simulation.Shapes, colliders.Length);
            try
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    // Dynamic bodies spread their mass over the children by volume.
                    var weight = type == PhysxBodyType.Dynamic ? mass * volumes[i] / totalVolume : 1f;
                    AddChild(ref builder, colliders[i], type, weight);
                }

                if (type == PhysxBodyType.Dynamic)
                {
                    builder.BuildDynamicCompound(out var children, out var inertia);
                    return new BodyShape(_simulation.Shapes.Add(new Compound(children)), inertia);
                }

                builder.BuildKinematicCompound(out var kinematicChildren);
                return new BodyShape(_simulation.Shapes.Add(new Compound(kinematicChildren)), default);
            }
            finally
            {
                builder.Dispose();
            }
        }

        private static void AddChild(ref CompoundBuilder builder, BepuCollider3D collider, PhysxBodyType type, float weight)
        {
            var size = collider.Transform.Size.ToVector3();
            var pose = LocalPose(collider);

            switch (collider.Shape)
            {
                case PhysxColliderShape3D.Sphere3D:
                    AddChild(ref builder, new Sphere(size.X), pose, type, weight);
                    break;
                case PhysxColliderShape3D.Box3D:
                    AddChild(ref builder, new Box(size.X * 2f, size.Y * 2f, size.Z * 2f), pose, type, weight);
                    break;
                case PhysxColliderShape3D.Capsule3D:
                    AddChild(ref builder, new Capsule(size.X, size.Y * 2f), pose, type, weight);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported convex collider shape: {collider.Shape}");
            }
        }

        private static void AddChild<TShape>(ref CompoundBuilder builder, TShape shape, RigidPose pose, PhysxBodyType type, float weight)
            where TShape : unmanaged, IConvexShape
        {
            if (type == PhysxBodyType.Dynamic)
                builder.Add(shape, pose, weight);
            else
                builder.AddForKinematic(shape, pose, weight);
        }

        private static void ValidateConvex(BepuCollider3D collider)
        {
            var size = collider.Transform.Size.ToVector3();

            bool valid = collider.Shape switch
            {
                PhysxColliderShape3D.Sphere3D => IsPositive(size.X),
                PhysxColliderShape3D.Box3D => IsPositive(size.X) && IsPositive(size.Y) && IsPositive(size.Z),
                PhysxColliderShape3D.Capsule3D => IsPositive(size.X) && float.IsFinite(size.Y) && size.Y >= 0f,
                _ => throw new NotSupportedException($"Unsupported convex collider shape: {collider.Shape}"),
            };

            if (!valid)
                throw new ArgumentOutOfRangeException(nameof(collider),
                    $"Collider '{collider.Id}' ({collider.Shape}) has a non-positive or non-finite size {size}.");
        }

        private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;

        private static float Volume(BepuCollider3D collider)
        {
            var size = collider.Transform.Size.ToVector3();
            return collider.Shape switch
            {
                PhysxColliderShape3D.Sphere3D => 4f / 3f * MathF.PI * size.X * size.X * size.X,
                PhysxColliderShape3D.Box3D => 8f * size.X * size.Y * size.Z,
                PhysxColliderShape3D.Capsule3D =>
                    MathF.PI * size.X * size.X * (2f * size.Y) + 4f / 3f * MathF.PI * size.X * size.X * size.X,
                _ => throw new NotSupportedException($"Unsupported convex collider shape: {collider.Shape}"),
            };
        }
    }
}
