/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// Settings of a standalone 2D physics world (a match simulation, a scratch world for
    /// prediction, ...). Iterations trade accuracy for speed; the defaults are Box2D's usual 8 / 3.
    /// </summary>
    public sealed record PhysxWorldSettings2D
    {
        public Vector2 Gravity { get; init; }
        public float FixedDeltaTime { get; init; } = 1f / 60f;
        public int VelocityIterations { get; init; } = 8;
        public int PositionIterations { get; init; } = 3;
    }

    /// <summary>Everything a body is created with.</summary>
    public readonly record struct PhysxBodyDef2D
    {
        public PhysxBodyType Type { get; init; }
        public Vector2 Position { get; init; }
        /// <summary>Radians.</summary>
        public float Angle { get; init; }
        /// <summary>Continuous collision against other dynamic bodies (fast movers).</summary>
        public bool Bullet { get; init; }
        public bool AllowSleep { get; init; }
        public bool FixedRotation { get; init; }
        public float LinearDamping { get; init; }
        public float AngularDamping { get; init; }
        /// <summary>Multiplier of the world gravity for this body.</summary>
        public float GravityScale { get; init; }
        /// <summary>Game data carried by the body (see <see cref="IPhysxBody2D.UserData"/>).</summary>
        public object? UserData { get; init; }
        /// <summary>Optional id; a new one is generated when null.</summary>
        public string? Id { get; init; }

        public PhysxBodyDef2D()
        {
            AllowSleep = true;
            GravityScale = 1f;
        }
    }

    public enum PhysxShapeKind2D { Circle, Polygon, Chain, Edge }

    /// <summary>
    /// A collision shape in body-local coordinates. Chains are one-sided (Box2D 2.4): a loop
    /// wound clockwise collides on its inside.
    /// </summary>
    public sealed class PhysxShape2D
    {
        public PhysxShapeKind2D Kind { get; }
        public float Radius { get; }
        public Vector2 Center { get; }
        public IReadOnlyList<Vector2> Vertices { get; }
        public bool Loop { get; }

        private PhysxShape2D(PhysxShapeKind2D kind, float radius, Vector2 center, Vector2[] vertices, bool loop)
        {
            Kind = kind;
            Radius = radius;
            Center = center;
            Vertices = vertices;
            Loop = loop;
        }

        public static PhysxShape2D Circle(float radius, Vector2 center = default) =>
            new(PhysxShapeKind2D.Circle, radius, center, Array.Empty<Vector2>(), false);

        /// <summary>A rectangle with the given half extents, optionally offset and rotated (radians).</summary>
        public static PhysxShape2D Box(float halfWidth, float halfHeight, Vector2 center = default, float angle = 0f)
        {
            var rotation = Rotation2D.FromRadians(angle);
            Vector2 At(float x, float y) => center + rotation.Rotate(new Vector2(x, y));
            return new PhysxShape2D(PhysxShapeKind2D.Polygon, 0f, center,
                new[] { At(-halfWidth, -halfHeight), At(halfWidth, -halfHeight), At(halfWidth, halfHeight), At(-halfWidth, halfHeight) }, false)
            {
                IsBox = true,
                HalfWidth = halfWidth,
                HalfHeight = halfHeight,
                BoxAngle = angle,
            };
        }

        /// <summary>A convex polygon (3..8 vertices); the engine computes the hull.</summary>
        public static PhysxShape2D Polygon(IEnumerable<Vector2> vertices) =>
            new(PhysxShapeKind2D.Polygon, 0f, default, vertices.ToArray(), false);

        /// <summary>A chain of edges; <paramref name="loop"/> closes it.</summary>
        public static PhysxShape2D Chain(IEnumerable<Vector2> vertices, bool loop) =>
            new(PhysxShapeKind2D.Chain, 0f, default, vertices.ToArray(), loop);

        /// <summary>A single two-sided edge.</summary>
        public static PhysxShape2D Edge(Vector2 a, Vector2 b) =>
            new(PhysxShapeKind2D.Edge, 0f, default, new[] { a, b }, false);

        // Boxes keep their parameters so the engine can build them exactly like a native box.
        internal bool IsBox { get; private init; }
        internal float HalfWidth { get; private init; }
        internal float HalfHeight { get; private init; }
        internal float BoxAngle { get; private init; }
    }

    /// <summary>
    /// Collision filtering: two fixtures collide when each one's category is in the other's mask,
    /// unless they share a group: a positive group always collides, a negative one never does.
    /// </summary>
    public readonly record struct PhysxFilter2D(ushort Category = 1, ushort Mask = 0xFFFF, short Group = 0)
    {
        public static readonly PhysxFilter2D Default = new(Category: 1, Mask: 0xFFFF, Group: 0);
    }

    /// <summary>A shape attached to a body, with its material and filter.</summary>
    public readonly record struct PhysxFixtureDef2D
    {
        public required PhysxShape2D Shape { get; init; }
        public float Density { get; init; }
        public float Friction { get; init; }
        public float Restitution { get; init; }
        public PhysxFilter2D Filter { get; init; }
        /// <summary>Sensor: reports contacts but does not collide.</summary>
        public bool IsTrigger { get; init; }
        /// <summary>Game data carried by the fixture (see <see cref="IPhysxCollider.UserData"/>).</summary>
        public object? UserData { get; init; }

        public PhysxFixtureDef2D()
        {
            Friction = 0.2f;
            Filter = PhysxFilter2D.Default;
        }
    }

    /// <summary>A fixture created from a <see cref="PhysxFixtureDef2D"/>.</summary>
    public interface IPhysxFixture2D : IPhysxCollider2D
    {
        IPhysxBody2D Body { get; }
        float Friction { get; set; }
        float Restitution { get; set; }
        float Density { get; }
        PhysxFilter2D Filter { get; set; }
    }

    /// <summary>The contact's world-space geometry: normal from A to B and up to two points.</summary>
    public readonly struct PhysxWorldManifold2D
    {
        public Vector2 Normal { get; }
        public int PointCount { get; }
        public Vector2 Point0 { get; }
        public Vector2 Point1 { get; }

        public PhysxWorldManifold2D(Vector2 normal, int pointCount, Vector2 point0, Vector2 point1)
        {
            Normal = normal;
            PointCount = pointCount;
            Point0 = point0;
            Point1 = point1;
        }

        public Vector2 this[int index] => index == 0 ? Point0 : Point1;

        /// <summary>The average of the contact points (zero without points).</summary>
        public Vector2 Midpoint
        {
            get
            {
                if (PointCount == 0) return Vector2.Zero;
                var p = Vector2.Zero;
                for (var i = 0; i < PointCount; i++) p += this[i];
                return p / PointCount;
            }
        }
    }

    /// <summary>
    /// A contact between two fixtures, valid only inside the listener call or the contact
    /// enumeration that handed it out.
    /// </summary>
    public interface IPhysxContact2D
    {
        IPhysxFixture2D FixtureA { get; }
        IPhysxFixture2D FixtureB { get; }
        IPhysxBody2D BodyA { get; }
        IPhysxBody2D BodyB { get; }
        /// <summary>The shapes overlap (or are within the solver's margin).</summary>
        bool IsTouching { get; }
        /// <summary>False when disabled for this step (pre-solve), e.g. one-way platforms.</summary>
        bool IsEnabled { get; set; }
        /// <summary>Overrides the mixed restitution for this contact (pre-solve).</summary>
        float Restitution { get; set; }
        /// <summary>Overrides the mixed friction for this contact (pre-solve).</summary>
        float Friction { get; set; }
        /// <summary>Points in the local manifold (0 when the shapes do not touch).</summary>
        int PointCount { get; }
        PhysxWorldManifold2D GetWorldManifold();
    }

    /// <summary>Contact callbacks of a world, all on the stepping thread, inside <c>Step</c>.</summary>
    public interface IPhysxContactListener2D
    {
        void BeginContact(IPhysxContact2D contact) { }
        void EndContact(IPhysxContact2D contact) { }
        /// <summary>After collision detection, before solving: may disable the contact or change its material.</summary>
        void PreSolve(IPhysxContact2D contact) { }
        /// <summary>After solving, with the largest normal impulse applied at the contact.</summary>
        void PostSolve(IPhysxContact2D contact, float normalImpulse) { }
    }

    /// <summary>
    /// Ray-cast callback, called per fixture hit in no particular order. Return -1 to ignore the
    /// fixture, 0 to stop, <paramref name="fraction"/> to clip the ray to this hit (closest hit),
    /// or 1 to continue unclipped (all hits).
    /// </summary>
    public interface IPhysxRayCastCallback2D
    {
        float OnHit(IPhysxFixture2D fixture, Vector2 point, Vector2 normal, float fraction);
    }
}
