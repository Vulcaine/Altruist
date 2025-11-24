// BepuPhysxBodyApiProvider3D.cs
// CreateBody has NO world parameter; returns a BEPU-backed IPhysxBody3D.
// Caller must then call world.AddBody(body) to register it with the world/engine.

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

using BepuPhysics;
using BepuPhysics.Collidables;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// BEPU-backed body API provider (3D).
    /// Creates BEPU bodies and attaches/detaches a single collider shape by swapping the body's shape.
    /// </summary>
    [Service(typeof(IPhysxBodyApiProvider3D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue:"3D")]
    public sealed class BepuPhysxBodyApiProvider3D : IPhysxBodyApiProvider3D
    {
        private readonly BepuWorldEngine3D _engine;

        // Store the original body shape so we can restore it after removing an attached collider.
        // One entry per body handle.
        private readonly Dictionary<BodyHandle, TypedIndex> _originalShapeByBody = new();

        // Track which collider is attached to which body and what shape it installed.
        private readonly Dictionary<IPhysxCollider3D, (BodyHandle Body, TypedIndex Shape)> _attachments = new();

        public BepuPhysxBodyApiProvider3D(IPhysxWorldEngine3D engine)
        {
            _engine = engine as BepuWorldEngine3D
                      ?? throw new InvalidOperationException("Engine must be a BEPU-backed engine.");
        }

        /// <summary>
        /// Attach a collider to a body by swapping the body's current shape for the collider's shape.
        /// Supports one collider via this API at a time per body (simple swap/restore model).
        /// </summary>
        public void AddCollider(IPhysxBody3D body, IPhysxCollider3D collider)
        {
            if (body is not BepuWorldEngine3D.Body3DAdapter owner)
                throw new InvalidOperationException("Body must be a BEPU-backed Body3DAdapter.");

            if (_attachments.ContainsKey(collider))
                throw new InvalidOperationException("This collider is already attached.");

            var bodies = _engine.Simulation.Bodies;
            var bodyRef = bodies.GetBodyReference(owner.Handle);

            // Remember original shape (first time we attach to this body)
            if (!_originalShapeByBody.ContainsKey(owner.Handle))
            {
                _originalShapeByBody[owner.Handle] = bodyRef.Collidable.Shape;
            }

            // Build a BEPU shape from collider data and add it to the shape registry
            var shapeIndex = CreateBepuShapeIndexFromCollider(collider);

            // Sensor/trigger in BEPU is usually handled at the collision filtering layer;
            // if you have a trigger pipeline, mark/filter there. We just set shape here.

            bodyRef.Collidable.Shape = shapeIndex;

            // Basic damping/continuous detection etc. can be configured elsewhere as needed.

            _attachments[collider] = (owner.Handle, shapeIndex);
        }

        /// <summary>
        /// Detach the collider: if it is currently attached, restore the body's original shape.
        /// </summary>
        public void RemoveCollider(IPhysxCollider3D collider)
        {
            if (!_attachments.TryGetValue(collider, out var rec))
                return;

            var handle = rec.Body;
            var bodies = _engine.Simulation.Bodies;
            var bodyRef = bodies.GetBodyReference(handle);

            if (_originalShapeByBody.TryGetValue(handle, out var original))
            {
                bodyRef.Collidable.Shape = original;
            }

            _attachments.Remove(collider);
            // Keep the original shape remembered for potential future attach/detach cycles.
            // If you want to fully forget after last detach, uncomment the following:
            // _originalShapeByBody.Remove(handle);
        }

        public IPhysxBody3D CreateBody(PhysxBodyType type, float mass, Transform3D transform)
        {
            var pos = transform.Position.ToVector3();
            var ori = transform.Rotation.ToQuaternion();
            var halfExtents = transform.Size.ToVector3();

            // Default box using transform size as half extents (so full size is doubled)
            var box = new Box(halfExtents.X * 2f, halfExtents.Y * 2f, halfExtents.Z * 2f);
            var shapeIndex = _engine.Simulation.Shapes.Add(box);

            var pose = new RigidPose(new System.Numerics.Vector3(pos.X, pos.Y, pos.Z), ori);

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

        // -------------------- helpers --------------------

        private TypedIndex CreateBepuShapeIndexFromCollider(IPhysxCollider3D c)
        {
            // The collider's Transform is *local* to the body. BEPU does not support per-shape local offsets
            // on a single-collider body without a compound. In this simple swap/restore model, we use only size info.
            // Local offsets/rotations would require building a Compound (can be added later).
            var t = c.Transform;

            switch (c.Shape)
            {
                case PhysxColliderShape3D.Sphere3D:
                    {
                        // Convention: Transform.Size.X stores radius
                        var radius = t.Size.X;
                        var sphere = new Sphere(radius);
                        return _engine.Simulation.Shapes.Add(sphere);
                    }

                case PhysxColliderShape3D.Box3D:
                    {
                        // Convention: Transform.Size stores half extents; BEPU needs full extents.
                        var fullX = t.Size.X * 2f;
                        var fullY = t.Size.Y * 2f;
                        var fullZ = t.Size.Z * 2f;
                        var box = new Box(fullX, fullY, fullZ);
                        return _engine.Simulation.Shapes.Add(box);
                    }

                case PhysxColliderShape3D.Capsule3D:
                    {
                        // Convention: Transform.Size.X = radius, Transform.Size.Y = half length -> length = 2 * halfLen
                        var radius = t.Size.X;
                        var length = t.Size.Y * 2f;
                        var capsule = new Capsule(radius, length);
                        return _engine.Simulation.Shapes.Add(capsule);
                    }

                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }
    }
}
