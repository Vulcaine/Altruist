/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// Engine-agnostic descriptor for a 3D body.
    /// Providers/engines interpret this to create their own body objects.
    /// </summary>
    public readonly struct PhysxBody3DDesc
    {
        /// <summary>Logical id for this body descriptor.</summary>
        public string Id { get; }

        public PhysxBodyType Type { get; }
        public float Mass { get; }

        public PhysxTag? PhysxTag { get; }

        /// <summary>
        /// If true, body should be created as kinematic (driven by user code, not forces).
        /// In BEPU this typically means inverse mass/inertia are zero and the game sets pose/velocity.
        /// </summary>
        public bool IsKinematic { get; }

        public Transform3D Transform { get; }

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
    /// Static factory for engine-agnostic body descriptors.
    /// No providers, no engines here.
    /// </summary>
    public static class PhysxBody3D
    {
        public static PhysxBody3DDesc Create(float mass, Size3D size, Position3D position, bool isKinematic = false, PhysxTag? physxTag = null)
        {
            var type = mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static;
            var transform = new Transform3D(position, size, Scale3D.One, Rotation3D.Identity);
            return Create(type, mass, transform, isKinematic, physxTag);
        }

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

    public interface IPhysxBody3D : IPhysxBody
    {
        System.Numerics.Vector3 Position { get; set; }
        System.Numerics.Quaternion Rotation { get; set; }
        System.Numerics.Vector3 LinearVelocity { get; set; }
        System.Numerics.Vector3 AngularVelocity { get; set; }
    }

    /// <summary>
    /// Provider API for creating engine-specific bodies from engine-agnostic descriptors.
    /// </summary>
    public interface IPhysxBodyApiProvider3D
    {
        /// <summary>
        /// Create an engine-specific body from a descriptor for the given world/engine.
        /// </summary>
        IPhysxBody3D CreateBody(IPhysxWorldEngine3D engine, in PhysxBody3DDesc desc);

        /// <summary>Attach a collider to a body (creates a fixture / swaps shape under the hood).</summary>
        void AddCollider(IPhysxWorldEngine3D engine, IPhysxBody3D body, IPhysxCollider3D collider);

        /// <summary>Detach and restore the body's original state if the collider was attached.</summary>
        void RemoveCollider(IPhysxWorldEngine3D engine, IPhysxCollider3D collider);
    }

    /// <summary>
    /// Provider API for creating engine-specific colliders from engine-agnostic descriptors.
    /// Note: colliders themselves are engine-agnostic; engines interpret them via AddCollider.
    /// </summary>
    public interface IPhysxColliderApiProvider3D
    {
        IPhysxCollider3D CreateCollider(in PhysxCollider3DDesc desc);
    }
}
