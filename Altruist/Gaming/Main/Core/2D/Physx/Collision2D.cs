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
    /// <para>Persistence-side description of a collider (stored through the vault). Nothing in the
    /// framework currently builds bodies from these models: JSON world files use
    /// <see cref="WorldColliderSchema2D"/> (loaded by <see cref="WorldLoader2D"/>), and runtime code
    /// creates colliders with <see cref="PhysxCollider2DParams"/>. Use these when you persist collider
    /// data yourself and map it onto <see cref="PhysxCollider2DParams"/>.</para>
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
        /// <summary>Optional collision-filter key (pure data; resolved by your own code).</summary>
        public string? CollisionFilterId { get; set; }
        /// <summary>Optional density overriding the material's (pure data; applied by your own code).</summary>
        public float? DensityOverride { get; set; }
    }

    /// <summary>Circle collider (radius read from Transform.Size.X or explicit Radius).</summary>
    public sealed class CircleCollider2DModel : Collider2DModel
    {
        /// <summary>Discriminator: <c>engine.collider2d.circle</c>.</summary>
        public override string Type { get; set; } = "engine.collider2d.circle";

        /// <summary>Storage key (empty until assigned).</summary>
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Optional explicit radius. If null, use Transform.Size.X (convention from your API).
        /// </summary>
        public float? Radius { get; set; }

        /// <summary>Creates a circle collider model (<see cref="Collider2DModel.Shape"/> = <see cref="PhysxColliderShape2D.Circle2D"/>).</summary>
        public CircleCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Circle2D;
        }
    }

    /// <summary>Axis-aligned box collider (half extents via Vector2).</summary>
    public sealed class BoxCollider2DModel : Collider2DModel
    {
        /// <summary>Discriminator: <c>engine.collider2d.box</c>.</summary>
        public override string Type { get; set; } = "engine.collider2d.box";
        /// <summary>Storage key (empty until assigned).</summary>
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Optional half extents. If null, use Transform.Size (as half extents) per your creation helpers.
        /// </summary>
        public Vector2? HalfExtents { get; set; }

        /// <summary>Creates a box collider model (<see cref="Collider2DModel.Shape"/> = <see cref="PhysxColliderShape2D.Box2D"/>).</summary>
        public BoxCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Box2D;
        }
    }

    /// <summary>Capsule collider (radius + half length along capsule axis).</summary>
    public sealed class CapsuleCollider2DModel : Collider2DModel
    {
        /// <summary>Discriminator: <c>engine.collider2d.capsule</c>.</summary>
        public override string Type { get; set; } = "engine.collider2d.capsule";
        /// <summary>Storage key (empty until assigned).</summary>
        public override string StorageId { get; set; } = "";

        /// <summary>Optional explicit radius; fallback to Transform.Size.X if null.</summary>
        public float? Radius { get; set; }

        /// <summary>Optional explicit half length; fallback to Transform.Size.Y if null.</summary>
        public float? HalfLength { get; set; }

        /// <summary>Creates a capsule collider model (<see cref="Collider2DModel.Shape"/> = <see cref="PhysxColliderShape2D.Capsule2D"/>).</summary>
        public CapsuleCollider2DModel()
        {
            Shape = PhysxColliderShape2D.Capsule2D;
        }
    }

    /// <summary>Polygon collider (arbitrary convex/concave depending on engine support).</summary>
    public sealed class PolygonCollider2DModel : Collider2DModel
    {
        /// <summary>Discriminator: <c>engine.collider2d.polygon</c>.</summary>
        public override string Type { get; set; } = "engine.collider2d.polygon";
        /// <summary>Storage key (empty until assigned).</summary>
        public override string StorageId { get; set; } = "";

        /// <summary>
        /// Vertex list in local space (relative to owning body). Persisted as an array for vault serialization.
        /// </summary>
        public Vector2[] Vertices { get; set; } = Array.Empty<Vector2>();

        /// <summary>Creates a polygon collider model (<see cref="Collider2DModel.Shape"/> = <see cref="PhysxColliderShape2D.Polygon2D"/>).</summary>
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
        /// <summary>Discriminator: <c>engine.body2d.spec</c>.</summary>
        public override string Type { get; set; } = "engine.body2d.spec";
        /// <summary>Last update timestamp.</summary>
        public override DateTime Timestamp { get; set; }
        /// <summary>Storage key (empty until assigned).</summary>
        public override string StorageId { get; set; } = "";

        /// <summary>Dynamic/Static/Kinematic behavior hint for IPhysxBody2D factory.</summary>
        public string BodyKind { get; set; } = "Dynamic"; // or "Static", "Kinematic"

        /// <summary>Optional defaults that colliders can inherit if not overridden.</summary>
        public string? DefaultMaterialId { get; set; }
        /// <summary>Optional default collision-filter key for colliders that do not set their own.</summary>
        public string? DefaultCollisionFilterId { get; set; }

        /// <summary>Optional mass override for dynamic bodies (engine-specific usage).</summary>
        public float? Mass { get; set; }
        /// <summary>Optional: when true the body should not rotate (applied by your own body factory).</summary>
        public bool? FixedRotation { get; set; }
    }
}
