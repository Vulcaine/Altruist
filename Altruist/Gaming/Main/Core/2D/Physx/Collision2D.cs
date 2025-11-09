/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>
    /// Base persisted collider model for 2D. Stores engine-agnostic properties
    /// that map 1:1 onto your PhysX creation API (shape + transform + trigger).
    /// Concrete shapes add their specific parameters.
    /// </summary>
    public abstract class Collider2DModel : VaultModel
    {
        /// <summary>Discriminator used by your infra; set a sensible default per concrete type.</summary>
        public override string Type { get; set; } = "engine.collider2d";

        /// <summary>Last update timestamp as required by IVaultModel.</summary>
        public override DateTime Timestamp { get; set; }

        /// <summary>The collider shape (Circle, Box, Capsule, Polygon).</summary>
        public PhysxColliderShape2D Shape { get; set; }

        /// <summary>Local transform relative to the owning body (position, rotation, size where applicable).</summary>
        public Transform2D Transform { get; set; } = Transform2D.Zero;

        /// <summary>If true, collider generates overlap events but no physical response.</summary>
        public bool IsTrigger { get; set; } = false;

        /// <summary>Optional physics/material/filter keys (pure data, not required by PhysX API).</summary>
        public string? MaterialId { get; set; }
        public string? CollisionFilterId { get; set; }
        public float? DensityOverride { get; set; }
    }

    /// <summary>Circle collider (radius read from Transform.Size.X or explicit Radius).</summary>
    public sealed class CircleCollider2DModel : Collider2DModel
    {
        public override string Type { get; set; } = "engine.collider2d.circle";

        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Optional explicit radius. If null, use Transform.Size.X (convention from your API).
        /// </summary>
        public float? Radius { get; set; }

        public CircleCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Circle2D;
        }
    }

    /// <summary>Axis-aligned box collider (half extents via Vector2).</summary>
    public sealed class BoxCollider2DModel : Collider2DModel
    {
        public override string Type { get; set; } = "engine.collider2d.box";
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Optional half extents. If null, use Transform.Size (as half extents) per your creation helpers.
        /// </summary>
        public Vector2? HalfExtents { get; set; }

        public BoxCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Box2D;
        }
    }

    /// <summary>Capsule collider (radius + half length along capsule axis).</summary>
    public sealed class CapsuleCollider2DModel : Collider2DModel
    {
        public override string Type { get; set; } = "engine.collider2d.capsule";
        public override string StorageId { get; set; } = "";

        /// <summary>Optional explicit radius; fallback to Transform.Size.X if null.</summary>
        public float? Radius { get; set; }

        /// <summary>Optional explicit half length; fallback to Transform.Size.Y if null.</summary>
        public float? HalfLength { get; set; }

        public CapsuleCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Capsule2D;
        }
    }

    /// <summary>Polygon collider (arbitrary convex/concave depending on engine support).</summary>
    public sealed class PolygonCollider2DModel : Collider2DModel
    {
        public override string Type { get; set; } = "engine.collider2d.polygon";
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Vertex list in local space (relative to owning body). Persisted as an array for vault serialization.
        /// </summary>
        public Vector2[] Vertices { get; set; } = Array.Empty<Vector2>();

        public PolygonCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Polygon2D;
        }
    }

    /// <summary>
    /// Optional: a lightweight, persisted "body spec" you can attach under a prefab root to group colliders,
    /// carry body-level flags (dynamic/static/kinematic), and default material/filter.
    /// Colliders remain separate children referencing this via GroupId (prefab.StorageId).
    /// </summary>
    public sealed class Body2DSpecModel : VaultModel
    {
        public override string Type { get; set; } = "engine.body2d.spec";
        public override DateTime Timestamp { get; set; }
        public override string StorageId { get; set; } = "";

        /// <summary>Dynamic/Static/Kinematic behavior hint for IPhysxBody2D factory.</summary>
        public string BodyKind { get; set; } = "Dynamic"; // or "Static", "Kinematic"

        /// <summary>Optional defaults that colliders can inherit if not overridden.</summary>
        public string? DefaultMaterialId { get; set; }
        public string? DefaultCollisionFilterId { get; set; }

        /// <summary>Optional mass override for dynamic bodies (engine-specific usage).</summary>
        public float? Mass { get; set; }
        public bool? FixedRotation { get; set; }
    }
}
