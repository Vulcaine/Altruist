/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// Base persisted collider model for 3D. Stores engine-agnostic properties
    /// that map onto your PhysX creation API (shape + transform + trigger).
    /// Concrete shapes add their specific parameters.
    /// </summary>
    public abstract class Collider3DModel : VaultModel
    {
        /// <summary>Discriminator used by infra; concrete classes override with specific type keys.</summary>
        public override string Type { get; set; } = "engine.collider3d";

        /// <summary>Required by IVaultModel.</summary>
        public override DateTime Timestamp { get; set; }

        /// <summary>The collider shape (Sphere, Box, Capsule, Mesh).</summary>
        public PhysxColliderShape3D Shape { get; set; }

        /// <summary>Local transform relative to the owning body (position, rotation, scale/size where applicable).</summary>
        public Transform3D Transform { get; set; } = Transform3D.Zero;

        /// <summary>If true, collider generates overlap events but no physical response.</summary>
        public bool IsTrigger { get; set; } = false;

        /// <summary>Optional physics/material/filter identifiers.</summary>
        public string? MaterialId { get; set; }
        public string? CollisionFilterId { get; set; }

        /// <summary>Optional density override (for dynamic bodies).</summary>
        public float? DensityOverride { get; set; }
    }

    /// <summary>Sphere collider (radius from explicit Radius or Transform.Scale.X if null).</summary>
    public sealed class SphereCollider3DModel : Collider3DModel
    {
        public override string Type { get; set; } = "engine.collider3d.sphere";
        public override string StorageId { get; set; } = "";

        /// <summary>Optional explicit radius; fallback to Transform.Scale.X if null.</summary>
        public float? Radius { get; set; }

        public SphereCollider3DModel()
        {
            Shape = PhysxColliderShape3D.Sphere3D;
        }
    }

    /// <summary>Axis-aligned box collider (half extents via Vector3).</summary>
    public sealed class BoxCollider3DModel : Collider3DModel
    {
        public override string Type { get; set; } = "engine.collider3d.box";
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Optional half extents. If null, use Transform.Scale as half extents per your creation helpers.
        /// </summary>
        public Vector3? HalfExtents { get; set; }

        public BoxCollider3DModel()
        {
            Shape = PhysxColliderShape3D.Box3D;
        }
    }

    /// <summary>Capsule collider (radius + half length along capsule axis).</summary>
    public sealed class CapsuleCollider3DModel : Collider3DModel
    {
        public override string Type { get; set; } = "engine.collider3d.capsule";
        public override string StorageId { get; set; } = "";

        /// <summary>Optional explicit radius; fallback to Transform.Scale.X if null.</summary>
        public float? Radius { get; set; }

        /// <summary>Optional explicit half length; fallback to Transform.Scale.Y if null.</summary>
        public float? HalfLength { get; set; }

        /// <summary>Axis choice (optional); if null, factory can infer from Transform or default to Y-up.</summary>
        public Vector3? Axis { get; set; }  // e.g., (0,1,0)

        public CapsuleCollider3DModel()
        {
            Shape = PhysxColliderShape3D.Capsule3D;
        }
    }

    /// <summary>
    /// Optional: a persisted "body spec" you can attach under a prefab root to group colliders,
    /// carry body-level flags (dynamic/static/kinematic), and default material/filter.
    /// Colliders remain separate children referencing this via GroupId (prefab.StorageId).
    /// </summary>
    public sealed class Body3DSpecModel : VaultModel
    {
        public override string Type { get; set; } = "engine.body3d.spec";
        public override DateTime Timestamp { get; set; }
        public override string StorageId { get; set; } = "";

        /// <summary>Dynamic/Static/Kinematic behavior hint for IPhysxBody3D factory.</summary>
        public string BodyKind { get; set; } = "Dynamic"; // "Static" | "Kinematic"

        /// <summary>Optional defaults that colliders can inherit if not overridden.</summary>
        public string? DefaultMaterialId { get; set; }
        public string? DefaultCollisionFilterId { get; set; }

        /// <summary>Optional mass override for dynamic bodies.</summary>
        public float? Mass { get; set; }

        /// <summary>Optionally freeze/lock body rotation (coarse switch). Engines may also expose axis locking.</summary>
        public bool? FixedRotation { get; set; }
    }
}
