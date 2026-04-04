/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;

using BepuPhysics;
using BepuPhysics.Collidables;

namespace Altruist.Physx.ThreeD
{
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
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

                // Default shape for the body descriptor is a box based on desc.Transform.Size.
                // (Colliders may later overwrite the shape via AddCollider.)
                var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
                var shapeIndex = engine3D.Simulation.Shapes.Add(box);

                // Statics use the statics set.
                if (desc.Type == PhysxBodyType.Static)
                {
                    var staticDesc = new StaticDescription(
                        new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                        ori,
                        shapeIndex
                    );

                    var staticHandle = engine3D.Simulation.Statics.Add(staticDesc);

                    return new BepuWorldEngine3D.StaticBody3DAdapter(
                        desc.Id,
                        engine3D,
                        staticHandle
                    );
                }

                // Everything else is a body in the Bodies set.
                var pose = new RigidPose(
                    new System.Numerics.Vector3(pos.X, pos.Y, pos.Z),
                    ori
                );

                var collidable = new CollidableDescription(shapeIndex, 0.1f);
                var activity = new BodyActivityDescription(0.01f);

                // Decide kinematic vs dynamic:
                // - Explicit flag wins
                // - Or explicit PhysxBodyType.Kinematic
                bool isKinematic = desc.IsKinematic || desc.Type == PhysxBodyType.Kinematic;

                BodyDescription bodyDesc;

                if (isKinematic)
                {
                    // Kinematic: infinite mass / no inertia. Driven by setting pose/velocity.
                    bodyDesc = BodyDescription.CreateKinematic(pose, collidable, activity);
                    bodyDesc.LocalInertia = default;
                }
                else
                {
                    // Dynamic: finite mass/inertia.
                    var useMass = desc.Mass > 0f ? desc.Mass : 1f;
                    var inertia = box.ComputeInertia(useMass);
                    bodyDesc = BodyDescription.CreateDynamic(pose, inertia, collidable, activity);
                }

                var handle = engine3D.Simulation.Bodies.Add(bodyDesc);

                // NOTE: You currently use DynamicBody3DAdapter for both dynamic and kinematic bodies.
                // That's fine as long as your engine logic respects Type and you don't apply gravity to kinematics.
                return new BepuWorldEngine3D.DynamicBody3DAdapter(
                    desc.Id,
                    engine3D,
                    handle,
                    isKinematic ? PhysxBodyType.Kinematic : PhysxBodyType.Dynamic,
                    isKinematic ? 0f : (desc.Mass > 0f ? desc.Mass : 1f)
                );
            }
        }

        public void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider)
        {
            if (engine is not BepuWorldEngine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            if (body is not BepuWorldEngine3D.Body3DAdapterBase adapter)
                throw new InvalidOperationException("Body must be a BEPU-backed body adapter.");

            adapter.AddCollider(collider);
        }

        public void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider)
        {
            if (engine is not BepuWorldEngine3D engine3D)
                throw new InvalidOperationException("Engine must be a BEPU-backed engine.");

            foreach (var b in engine3D.Bodies)
            {
                if (b is not BepuWorldEngine3D.Body3DAdapterBase adapter)
                    continue;

                if (adapter.TryGetColliderById(collider.Id, out var found) && ReferenceEquals(found, collider))
                {
                    adapter.RemoveCollider(collider);
                    return;
                }
            }
        }
    }
}
