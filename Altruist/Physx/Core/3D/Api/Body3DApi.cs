/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// Engine-agnostic descriptor for a 3D body. Build it with the <see cref="PhysxBody3D"/> factory
    /// and pass it to <see cref="IPhysxBodyApiProvider3D.CreateBody"/>.
    /// </summary>
    public readonly struct PhysxBody3DDesc
    {
        /// <summary>Logical id for this body descriptor.</summary>
        public string Id { get; }

        /// <summary>Simulation role of the body.</summary>
        public PhysxBodyType Type { get; }
        /// <summary>Mass; the BEPU backend substitutes 1 for non-positive values on dynamic bodies and ignores it for static/kinematic ones.</summary>
        public float Mass { get; }

        /// <summary>Optional collision-layer tag used by query layer masks; <see langword="null"/> means "all layers".</summary>
        public PhysxTag? PhysxTag { get; }

        /// <summary>
        /// If true, body should be created as kinematic (driven by user code, not forces).
        /// In BEPU this typically means inverse mass/inertia are zero and the game sets pose/velocity.
        /// </summary>
        public bool IsKinematic { get; }

        /// <summary>
        /// Initial pose and default shape. <c>Position</c>/<c>Rotation</c> give the starting pose; <c>Size</c> holds the half
        /// extents of the default box shape used until a collider is attached.
        /// </summary>
        public Transform3D Transform { get; }

        /// <summary>Creates a descriptor. Prefer the <see cref="PhysxBody3D"/> factory, which generates the id.</summary>
        /// <param name="id">Unique body id.</param>
        /// <param name="type">Simulation role.</param>
        /// <param name="mass">Body mass.</param>
        /// <param name="isKinematic">Forces a kinematic body regardless of <paramref name="type"/>.</param>
        /// <param name="transform">Initial pose and default box half extents.</param>
        /// <param name="physxTag">Optional layer tag.</param>
        public PhysxBody3DDesc(string id, PhysxBodyType type, float mass, bool isKinematic, Transform3D transform, PhysxTag? physxTag = null)
        {
            Id = id;
            Type = type;
            Mass = mass;
            IsKinematic = isKinematic;
            Transform = transform;
            PhysxTag = physxTag;
        }
    }

    /// <summary>
    /// Static factory for engine-agnostic body descriptors (pure data, no engine access). Each call generates a new
    /// GUID id (32 hex digits, no dashes).
    /// </summary>
    /// <example>
    /// <code>
    /// var desc  = PhysxBody3D.Create(mass: 1f, Size3D.Of(0.5f, 0.5f, 0.5f), Position3D.Of(0, 5, 0));
    /// var body  = bodyApi.CreateBody(engine, desc);
    /// bodyApi.AddCollider(engine, body, colliderApi.CreateCollider(PhysxCollider3D.CreateSphere(0.5f)));
    /// engine.AddBody(body);
    /// </code>
    /// </example>
    public static class PhysxBody3D
    {
        /// <summary>
        /// Creates a descriptor at <paramref name="position"/> with identity rotation. The type is inferred from mass:
        /// <c>mass &gt; 0</c> → <see cref="PhysxBodyType.Dynamic"/>, otherwise <see cref="PhysxBodyType.Static"/>
        /// (<paramref name="isKinematic"/> overrides both).
        /// </summary>
        /// <param name="mass">Body mass; ≤ 0 makes a static body.</param>
        /// <param name="size">Half extents of the default box shape.</param>
        /// <param name="position">Initial world position.</param>
        /// <param name="isKinematic">When <see langword="true"/>, creates a kinematic body (moved by code, unaffected by forces and gravity).</param>
        /// <param name="physxTag">Optional layer tag.</param>
        public static PhysxBody3DDesc Create(float mass, Size3D size, Position3D position, bool isKinematic = false, PhysxTag? physxTag = null)
        {
            var type = mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static;
            var transform = new Transform3D(position, size, Scale3D.One, Rotation3D.Identity);
            return Create(type, mass, transform, isKinematic, physxTag);
        }

        /// <summary>Creates a descriptor with an explicit type and full transform.</summary>
        /// <param name="type">Simulation role; replaced by <see cref="PhysxBodyType.Kinematic"/> when <paramref name="isKinematic"/> is set.</param>
        /// <param name="mass">Body mass.</param>
        /// <param name="transform">Initial pose; <c>Size</c> is the default box's half extents.</param>
        /// <param name="isKinematic">Forces a kinematic body.</param>
        /// <param name="physxTag">Optional layer tag.</param>
        public static PhysxBody3DDesc Create(PhysxBodyType type, float mass, Transform3D transform, bool isKinematic = false, PhysxTag? physxTag = null)
        {
            // If explicitly kinematic, treat as kinematic regardless of mass.
            // (Engine/provider can still validate.)
            if (isKinematic)
                type = PhysxBodyType.Kinematic;

            var id = Guid.NewGuid().ToString("N");
            return new PhysxBody3DDesc(id, type, mass, isKinematic, transform, physxTag: physxTag);
        }
    }

    /// <summary>
    /// 3D rigid body. Units: world units, seconds, radians; +Y is up and yaw 0 faces +Z (see <see cref="BodySteeringExtensions3D"/>).
    /// </summary>
    /// <remarks>
    /// For intent-level helpers (face, turn toward, move toward, launch) use <see cref="BodySteeringExtensions3D"/>
    /// rather than writing these properties by hand. In the BEPU backend every property access takes the engine lock,
    /// and setters wake the body.
    /// </remarks>
    public interface IPhysxBody3D : IPhysxBody
    {
        /// <summary>World-space position of the body's centre.</summary>
        System.Numerics.Vector3 Position { get; set; }
        /// <summary>World-space orientation.</summary>
        System.Numerics.Quaternion Rotation { get; set; }
        /// <summary>Linear velocity in world units per second.</summary>
        System.Numerics.Vector3 LinearVelocity { get; set; }
        /// <summary>Angular velocity in radians per second (axis × rate, world space).</summary>
        System.Numerics.Vector3 AngularVelocity { get; set; }
    }

    /// <summary>
    /// Creates engine-specific bodies from engine-agnostic descriptors and attaches colliders. Registered in DI by the
    /// active backend (BEPU: <see cref="BepuPhysxBodyApiProvider3D"/>, when <c>altruist:environment:mode</c> is <c>3D</c>).
    /// </summary>
    public interface IPhysxBodyApiProvider3D
    {
        /// <summary>
        /// Creates an engine-specific body from <paramref name="desc"/> and inserts it into <paramref name="engine"/>'s
        /// simulation. Call <see cref="IPhysxWorldEngine3D.AddBody"/> afterwards so the engine tracks it.
        /// </summary>
        /// <param name="engine">Target engine; must belong to the same backend.</param>
        /// <param name="desc">Body descriptor.</param>
        /// <exception cref="InvalidOperationException"><paramref name="engine"/> is from another backend.</exception>
        IPhysxBody3D CreateBody(IPhysxWorldEngine3D engine, in PhysxBody3DDesc desc);

        /// <summary>
        /// Attaches <paramref name="collider"/> to <paramref name="body"/>. In the BEPU backend this replaces the body's
        /// current shape (one active shape per body; the last attached collider wins) and wakes the body.
        /// </summary>
        /// <param name="engine">Engine owning the body.</param>
        /// <param name="body">Body created by this provider.</param>
        /// <param name="collider">Collider to attach.</param>
        /// <exception cref="InvalidOperationException">The engine or body is from another backend.</exception>
        void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider);

        /// <summary>
        /// Finds the registered body that owns <paramref name="collider"/>, detaches it and restores the body's original
        /// (descriptor) shape. No-op when no registered body owns it.
        /// </summary>
        /// <param name="engine">Engine owning the body.</param>
        /// <param name="collider">Collider to detach.</param>
        void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider);
    }

    /// <summary>
    /// Creates colliders from engine-agnostic <see cref="PhysxCollider3DDesc"/> descriptors. The colliders are data-only;
    /// engines interpret them when <see cref="IPhysxBodyApiProvider3D.AddCollider"/> is called.
    /// </summary>
    public interface IPhysxColliderApiProvider3D
    {
        /// <summary>Creates a collider carrying the descriptor's id, shape, transform, heightfield and trigger flag.</summary>
        /// <param name="desc">Descriptor built with <see cref="PhysxCollider3D"/>.</param>
        IPhysxCollider3D CreateCollider(in PhysxCollider3DDesc desc);
    }
}
