// ColliderApi2D.cs

using System.Numerics;

using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// Description of a primitive collider for <see cref="IPhysxColliderApiProvider2D.CreateCollider"/>.
    /// Size conventions (read by the Box2D body provider): <see cref="PhysxColliderShape2D.Circle2D"/>
    /// radius = <c>Transform.Size.X</c>; <see cref="PhysxColliderShape2D.Box2D"/> half extents =
    /// <c>Transform.Size</c>; <see cref="PhysxColliderShape2D.Capsule2D"/> radius = <c>Size.X</c>,
    /// half length = <c>Size.Y</c>. <c>Transform.Position</c> / <c>Rotation</c> are the offset in the body.
    /// </summary>
    public readonly struct PhysxCollider2DParams
    {
        /// <summary>Shape kind.</summary>
        public PhysxColliderShape2D Shape { get; }

        /// <summary>Local offset, rotation and size (see the type summary for the size convention).</summary>
        public Transform2D Transform { get; }

        /// <summary>Sensor: reports overlaps, no collision response.</summary>
        public bool IsTrigger { get; }

        /// <summary>Creates the description.</summary>
        /// <param name="shape">Shape kind.</param>
        /// <param name="transform">Local offset, rotation and size.</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public PhysxCollider2DParams(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger)
        {
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    /// <summary>Description of a convex polygon collider for <see cref="IPhysxPolygon2DProvider.CreatePolygon"/>.</summary>
    public readonly struct PhysxPolygon2DParams
    {
        /// <summary>Polygon vertices in the collider's local space (before <see cref="Transform"/>).</summary>
        public ReadOnlyMemory<Vector2> Vertices { get; }

        /// <summary>Local offset and rotation in the body.</summary>
        public Transform2D Transform { get; }

        /// <summary>Sensor: reports overlaps, no collision response.</summary>
        public bool IsTrigger { get; }

        /// <summary>Creates the description.</summary>
        /// <param name="vertices">Local polygon vertices.</param>
        /// <param name="transform">Local offset and rotation.</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public PhysxPolygon2DParams(ReadOnlyMemory<Vector2> vertices, Transform2D transform, bool isTrigger)
        {
            Vertices = vertices;
            Transform = transform;
            IsTrigger = isTrigger;
        }
    }

    /// <summary>
    /// Creates detached collider descriptions for the world-organizer path; attach them with
    /// <see cref="IPhysxBodyApiProvider2D.AddCollider"/>. DI service (the Box2D implementation is
    /// registered when <c>altruist:environment:mode</c> is <c>2D</c>). For standalone worlds use
    /// <see cref="Altruist.Physx.IPhysxWorldEngine2D.CreateFixture"/> with a <see cref="PhysxFixtureDef2D"/>
    /// instead (material, filter, chains and edges).
    /// </summary>
    public interface IPhysxColliderApiProvider2D
    {
        /// <summary>Creates a collider (not yet attached to any body).</summary>
        /// <param name="p">Shape, local transform and trigger flag.</param>
        IPhysxCollider2D CreateCollider(in PhysxCollider2DParams p);
    }

    /// <summary>Optional provider for polygon colliders (used by <see cref="PhysxCollider2D.CreatePolygon"/>).</summary>
    public interface IPhysxPolygon2DProvider
    {
        /// <summary>Creates a polygon collider (not yet attached to any body).</summary>
        /// <param name="p">Vertices, local transform and trigger flag.</param>
        IPhysxCollider2D CreatePolygon(in PhysxPolygon2DParams p);
    }

    /// <summary>
    /// Static shortcuts for creating colliders. <see cref="Provider"/> (and, for polygons,
    /// <see cref="PolygonProvider"/>) must be assigned first; nothing in the framework assigns them.
    /// Prefer injecting <see cref="IPhysxColliderApiProvider2D"/>.
    /// <example><code>
    /// var c = PhysxCollider2D.CreateCircle(0.5f);
    /// bodyProvider.AddCollider(body, c);
    /// </code></example>
    /// </summary>
    public static class PhysxCollider2D
    {
        /// <summary>Provider for circle / box / capsule colliders (process-wide).</summary>
        public static IPhysxColliderApiProvider2D Provider { get; set; } = default!;

        /// <summary>Provider for polygon colliders; null makes <see cref="CreatePolygon"/> throw.</summary>
        public static IPhysxPolygon2DProvider? PolygonProvider { get; set; }

        /// <summary>Creates a collider from an explicit shape and transform (size in
        /// <c>transform.Size</c>, see <see cref="PhysxCollider2DParams"/>).</summary>
        /// <param name="shape">Shape kind.</param>
        /// <param name="transform">Local offset, rotation and size.</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static IPhysxCollider2D Create(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger = false)
            => Provider.CreateCollider(new PhysxCollider2DParams(shape, transform, isTrigger));

        /// <summary>A circle of <paramref name="radius"/>; the transform's size is overwritten.</summary>
        /// <param name="radius">Radius in world units.</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static IPhysxCollider2D CreateCircle(float radius, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, 0f));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Circle2D, t, isTrigger));
        }

        /// <summary>A rectangle with the given half extents; the transform's size is overwritten.</summary>
        /// <param name="halfExtents">Half width (X) and half height (Y).</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static IPhysxCollider2D CreateRectangle(Vector2 halfExtents, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.From(halfExtents));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Box2D, t, isTrigger));
        }

        /// <summary>A capsule description. Note: the Box2D body provider approximates it as a box of
        /// half extents (<paramref name="halfLength"/>, <paramref name="radius"/>), with no rounded caps.</summary>
        /// <param name="radius">Capsule radius.</param>
        /// <param name="halfLength">Half length of the straight segment (along local X).</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static IPhysxCollider2D CreateCapsule(float radius, float halfLength, Transform2D? transform = null, bool isTrigger = false)
        {
            var t = (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, halfLength));
            return Provider.CreateCollider(new PhysxCollider2DParams(PhysxColliderShape2D.Capsule2D, t, isTrigger));
        }

        /// <summary>A convex polygon through <see cref="PolygonProvider"/> (vertices are copied).</summary>
        /// <param name="vertices">Local vertices.</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        /// <exception cref="InvalidOperationException"><see cref="PolygonProvider"/> is not set.</exception>
        public static IPhysxCollider2D CreatePolygon(ReadOnlySpan<Vector2> vertices, Transform2D? transform = null, bool isTrigger = false)
        {
            if (PolygonProvider is null)
                throw new InvalidOperationException("PhysxCollider2D.PolygonProvider is not set.");
            return PolygonProvider.CreatePolygon(new PhysxPolygon2DParams(
                vertices.ToArray(),
                transform ?? Transform2D.Zero,
                isTrigger));
        }
    }
}
