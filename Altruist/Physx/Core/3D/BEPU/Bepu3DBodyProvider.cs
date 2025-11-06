// BepuPhysxBodyApiProvider3D.cs
// CreateBody has NO world parameter; returns a BEPU-backed IPhysxBody3D.
// Caller must then call world.AddBody(body) to register it with the world/engine.

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace Altruist.Physx.ThreeD
{
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        private readonly BepuWorldEngine3D _engine;

        public BepuPhysxBodyApiProvider3D(IPhysxWorldEngine3D engine)
        {
            _engine = engine as BepuWorldEngine3D
                      ?? throw new InvalidOperationException("Engine must be a BEPU-backed engine.");
        }

        public IPhysxBody3D CreateBody(PhysxBodyType type, float mass, Transform3D transform)
        {
            var pos = transform.Position.ToVector3();
            var ori = transform.Rotation.ToQuaternion();
            var halfExtents = transform.Size.ToVector3();

            var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
            var shapeIndex = _engine.Simulation.Shapes.Add(box);

            var pose = new RigidPose(pos, ori);

            BodyInertia inertia = default;
            if (type == PhysxBodyType.Dynamic)
            {
                var useMass = mass > 0f ? mass : 1f;
                inertia = box.ComputeInertia(useMass);
            }

            var collidable = new CollidableDescription(shapeIndex, 0.1f);
            var activity = new BodyActivityDescription(0.01f);

            BodyDescription desc = type switch
            {
                PhysxBodyType.Dynamic => BodyDescription.CreateDynamic(pose, inertia, collidable, activity),
                PhysxBodyType.Kinematic => BodyDescription.CreateKinematic(pose, collidable, activity),
                _ => BodyDescription.CreateKinematic(pose, collidable, activity)
            };

            if (type != PhysxBodyType.Dynamic)
            {
                desc.LocalInertia = default;
            }

            var handle = _engine.Simulation.Bodies.Add(desc);
            var id = Guid.NewGuid().ToString("N");
            var adapter = new BepuWorldEngine3D.Body3DAdapter(id, _engine, handle, type, mass > 0f ? mass : 0f);


            return adapter;
        }
    }
}
