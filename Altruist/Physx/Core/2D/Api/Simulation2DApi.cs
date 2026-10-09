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
    /// <para>Pass to <see cref="Altruist.Physx.PhysxWorldEngine2D.Create"/> or
    /// <see cref="Altruist.Physx.IPhysxWorldEngineFactory2D.Create(PhysxWorldSettings2D)"/>.</para>
    /// <example><code>
    /// var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D
    /// {
    ///     Gravity = new Vector2(0, -9.81f),   // +Y up
    ///     FixedDeltaTime = 1f / 60f,
    ///     MaxSubSteps = 4,                    // opt-in fixed stepping
    /// });
    /// </code></example>
    /// </summary>
    public sealed record PhysxWorldSettings2D
    {
        /// <summary>World gravity in units/s² (+Y up, so downward gravity has a negative Y). Use zero
        /// to apply gravity by hand per body (<see cref="Velocity2D.ApplyGravity"/>).</summary>
        public Vector2 Gravity { get; init; }
        /// <summary>
        /// The fixed step of the world. Only used for stepping when <see cref="MaxSubSteps"/> is
        /// above zero; otherwise <c>Step(dt)</c> advances the world by exactly <c>dt</c>.
        /// </summary>
        public float FixedDeltaTime { get; init; } = 1f / 60f;

        /// <summary>Box2D velocity solver iterations per step (default 8).</summary>
        public int VelocityIterations { get; init; } = 8;

        /// <summary>Box2D position solver iterations per step (default 3).</summary>
        public int PositionIterations { get; init; } = 3;
        /// <summary>
        /// Fixed stepping (opt-in). 0 (default): <c>Step(dt)</c> runs one step of <c>dt</c>. Above
        /// zero: <c>Step(dt)</c> adds <c>dt</c> to an accumulator and runs whole steps of
        /// <see cref="FixedDeltaTime"/> while it lasts, at most this many per call; the time
        /// beyond that is dropped (no catch-up spiral), the remainder carries to the next call.
        /// </summary>
        public int MaxSubSteps { get; init; }
    }

    /// <summary>
    /// Everything a body is created with. The zero value (<c>default</c> or <c>new()</c>) is a
    /// valid definition: a <see cref="PhysxBodyType.Static"/> body at the origin that may sleep,
    /// with a gravity scale of 1.
    /// </summary>
    public readonly record struct PhysxBodyDef2D
    {
        private static readonly int OneBits = BitConverter.SingleToInt32Bits(1f);

        // Stored relative to the defaults so the zero value of the struct means AllowSleep = true
        // and GravityScale = 1 (exact: the scale's bits are XOR-ed with those of 1).
        private readonly bool _noSleep;
        private readonly int _gravityScaleBits;

        /// <summary>
        /// Static (the default, as in Box2D: <c>b2_staticBody</c>), Dynamic or Kinematic. Set
        /// <see cref="PhysxBodyType.Dynamic"/> for a body that moves under forces and contacts.
        /// </summary>
        public PhysxBodyType Type { get; init; }

        /// <summary>Initial body origin (world units).</summary>
        public Vector2 Position { get; init; }
        /// <summary>Initial rotation in radians, counter-clockwise.</summary>
        public float Angle { get; init; }
        /// <summary>Continuous collision against other dynamic bodies (fast movers).</summary>
        public bool Bullet { get; init; }
        /// <summary>The body may fall asleep when it comes to rest (default true).</summary>
        public bool AllowSleep
        {
            get => !_noSleep;
            init => _noSleep = !value;
        }
        /// <summary>Prevents rotation from contacts and torques (the angle can still be set).</summary>
        public bool FixedRotation { get; init; }

        /// <summary>Engine-side linear damping in 1/s, applied every step (default 0). For damping applied
        /// only where you choose, use <see cref="Velocity2D.ApplyLinearDrag"/> instead.</summary>
        public float LinearDamping { get; init; }

        /// <summary>Engine-side angular damping in 1/s (default 0).</summary>
        public float AngularDamping { get; init; }
        /// <summary>Multiplier of the world gravity for this body (default 1).</summary>
        public float GravityScale
        {
            get => BitConverter.Int32BitsToSingle(_gravityScaleBits ^ OneBits);
            init => _gravityScaleBits = BitConverter.SingleToInt32Bits(value) ^ OneBits;
        }
        /// <summary>Game data carried by the body (see <see cref="IPhysxBody2D.UserData"/>).</summary>
        public object? UserData { get; init; }
        /// <summary>Optional id; a new one is generated when null.</summary>
        public string? Id { get; init; }
    }

    /// <summary>Kind of a <see cref="PhysxShape2D"/>.</summary>
    public enum PhysxShapeKind2D
    {
        /// <summary>Circle (<see cref="PhysxShape2D.Radius"/>, <see cref="PhysxShape2D.Center"/>).</summary>
        Circle,
        /// <summary>Convex polygon, including boxes.</summary>
        Polygon,
        /// <summary>Chain of one-sided edges (open or loop).</summary>
        Chain,
        /// <summary>Single two-sided edge.</summary>
        Edge
    }

    /// <summary>
    /// A collision shape in body-local coordinates. Chains are one-sided (Box2D 2.4): a loop
    /// wound clockwise collides on its inside. Immutable; create with the static factories and pass in
    /// <see cref="PhysxFixtureDef2D.Shape"/>.
    /// </summary>
    public sealed class PhysxShape2D
    {
        /// <summary>The shape kind.</summary>
        public PhysxShapeKind2D Kind { get; }

        /// <summary>Circle radius (0 for other kinds).</summary>
        public float Radius { get; }

        /// <summary>Circle center or box center in body-local space (default for polygons from vertices, chains, edges).</summary>
        public Vector2 Center { get; }

        /// <summary>Body-local vertices (boxes: the four corners; empty for circles).</summary>
        public IReadOnlyList<Vector2> Vertices { get; }

        /// <summary>For chains: the chain closes into a loop.</summary>
        public bool Loop { get; }

        private PhysxShape2D(PhysxShapeKind2D kind, float radius, Vector2 center, Vector2[] vertices, bool loop)
        {
            Kind = kind;
            Radius = radius;
            Center = center;
            Vertices = vertices;
            Loop = loop;
        }

        /// <summary>A circle of <paramref name="radius"/> centered at body-local <paramref name="center"/>.</summary>
        /// <param name="radius">Radius in world units.</param>
        /// <param name="center">Body-local center (default origin).</param>
        public static PhysxShape2D Circle(float radius, Vector2 center = default) =>
            new(PhysxShapeKind2D.Circle, radius, center, Array.Empty<Vector2>(), false);

        /// <summary>A rectangle with the given half extents, optionally offset and rotated (radians,
        /// counter-clockwise). Built with Box2D's native <c>SetAsBox</c>, so it matches a hand-made Box2D box bit for bit.</summary>
        /// <param name="halfWidth">Half extent along local X.</param>
        /// <param name="halfHeight">Half extent along local Y.</param>
        /// <param name="center">Body-local center (default origin).</param>
        /// <param name="angle">Rotation in radians (default 0).</param>
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
        /// <param name="vertices">Body-local vertices (copied).</param>
        public static PhysxShape2D Polygon(IEnumerable<Vector2> vertices) =>
            new(PhysxShapeKind2D.Polygon, 0f, default, vertices.ToArray(), false);

        /// <summary>A chain of one-sided edges; <paramref name="loop"/> closes it. An open chain is created
        /// with its own first and last vertex as the (degenerate) ghost vertices.</summary>
        /// <param name="vertices">Body-local vertices (copied; at least 2 for a chain, 3 for a loop).</param>
        /// <param name="loop">Close the chain.</param>
        public static PhysxShape2D Chain(IEnumerable<Vector2> vertices, bool loop) =>
            new(PhysxShapeKind2D.Chain, 0f, default, vertices.ToArray(), loop);

        /// <summary>A single two-sided edge from <paramref name="a"/> to <paramref name="b"/> (body-local).</summary>
        /// <param name="a">First end point.</param>
        /// <param name="b">Second end point.</param>
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
    /// The zero value (<c>default</c> or <c>new()</c>) equals <see cref="Default"/>: category 1,
    /// mask 0xFFFF (everything), group 0.
    /// </summary>
    public readonly record struct PhysxFilter2D
    {
        /// <summary>Category 1, mask 0xFFFF, group 0 (same as <c>default</c>).</summary>
        public static readonly PhysxFilter2D Default = new(Category: 1, Mask: 0xFFFF, Group: 0);

        // Stored XOR-ed with the defaults so the zero value of the struct is the default filter.
        private const ushort DefaultCategory = 1;
        private const ushort DefaultMask = 0xFFFF;
        private readonly ushort _category;
        private readonly ushort _mask;

        /// <summary>Creates a filter.</summary>
        /// <param name="Category">Category bits of this fixture (default 1).</param>
        /// <param name="Mask">Categories this fixture collides with (default 0xFFFF).</param>
        /// <param name="Group">Group index: same positive group always collides, same negative never; 0 = none.</param>
        public PhysxFilter2D(ushort Category = DefaultCategory, ushort Mask = DefaultMask, short Group = 0)
        {
            _category = (ushort)(Category ^ DefaultCategory);
            _mask = (ushort)(Mask ^ DefaultMask);
            this.Group = Group;
        }

        /// <summary>Category bits of this fixture.</summary>
        public ushort Category
        {
            get => (ushort)(_category ^ DefaultCategory);
            init => _category = (ushort)(value ^ DefaultCategory);
        }

        /// <summary>Categories this fixture collides with.</summary>
        public ushort Mask
        {
            get => (ushort)(_mask ^ DefaultMask);
            init => _mask = (ushort)(value ^ DefaultMask);
        }

        /// <summary>Group index (see the type summary); 0 = no group.</summary>
        public short Group { get; init; }

        /// <summary>Deconstructs into (Category, Mask, Group).</summary>
        /// <param name="Category">Category bits.</param>
        /// <param name="Mask">Mask bits.</param>
        /// <param name="Group">Group index.</param>
        public void Deconstruct(out ushort Category, out ushort Mask, out short Group)
        {
            Category = this.Category;
            Mask = this.Mask;
            Group = this.Group;
        }
    }

    /// <summary>A shape attached to a body, with its material and filter. Pass to
    /// <see cref="Altruist.Physx.IPhysxWorldEngine2D.CreateFixture"/>. Defaults: density 0, friction 0.2,
    /// restitution 0, default filter, not a sensor.</summary>
    public readonly record struct PhysxFixtureDef2D
    {
        /// <summary>The collision shape (body-local).</summary>
        public required PhysxShape2D Shape { get; init; }

        /// <summary>Mass per area; the body's mass is derived from its fixtures' densities (a dynamic body
        /// whose fixtures all have density 0 gets Box2D's default mass of 1).</summary>
        public float Density { get; init; }

        /// <summary>Coulomb friction (default 0.2); mixed per contact as <c>sqrt(a * b)</c> by Box2D.</summary>
        public float Friction { get; init; }

        /// <summary>Bounciness 0..1 (default 0); mixed per contact as <c>max(a, b)</c> by Box2D.</summary>
        public float Restitution { get; init; }
        /// <summary>Collision filter; the default collides with everything (<see cref="PhysxFilter2D.Default"/>).</summary>
        public PhysxFilter2D Filter { get; init; }
        /// <summary>Sensor: reports contacts but does not collide.</summary>
        public bool IsTrigger { get; init; }
        /// <summary>Game data carried by the fixture (see <see cref="IPhysxCollider.UserData"/>).</summary>
        public object? UserData { get; init; }

        /// <summary>Creates a definition with friction 0.2 (Box2D's default); set <see cref="Shape"/>.</summary>
        public PhysxFixtureDef2D()
        {
            Friction = 0.2f;
        }
    }

    /// <summary>A fixture created from a <see cref="PhysxFixtureDef2D"/> (by
    /// <see cref="Altruist.Physx.IPhysxWorldEngine2D.CreateFixture"/>). Its <c>UserData</c> is the tag
    /// <see cref="ContactRouter2D"/> dispatches on.</summary>
    public interface IPhysxFixture2D : IPhysxCollider2D
    {
        /// <summary>The body the fixture is attached to.</summary>
        IPhysxBody2D Body { get; }

        /// <summary>Friction; changing it affects new contacts only (use <see cref="IPhysxContact2D.Friction"/>
        /// in pre-solve for existing ones).</summary>
        float Friction { get; set; }

        /// <summary>Restitution; changing it affects new contacts only (use
        /// <see cref="IPhysxContact2D.Restitution"/> in pre-solve for existing ones).</summary>
        float Restitution { get; set; }

        /// <summary>Density as created.</summary>
        float Density { get; }

        /// <summary>Collision filter (see <see cref="PhysxFilter2D"/>), read from and written to the native fixture.</summary>
        PhysxFilter2D Filter { get; set; }
    }

    /// <summary>The contact's world-space geometry: normal from A to B and up to two points.</summary>
    public readonly struct PhysxWorldManifold2D
    {
        /// <summary>Unit normal from fixture A to fixture B (world).</summary>
        public Vector2 Normal { get; }

        /// <summary>Number of valid points (0..2).</summary>
        public int PointCount { get; }

        /// <summary>First contact point (zero when <see cref="PointCount"/> is 0).</summary>
        public Vector2 Point0 { get; }

        /// <summary>Second contact point (zero when <see cref="PointCount"/> &lt; 2).</summary>
        public Vector2 Point1 { get; }

        /// <summary>Creates a manifold.</summary>
        /// <param name="normal">Unit normal A → B.</param>
        /// <param name="pointCount">Valid points (0..2).</param>
        /// <param name="point0">First point.</param>
        /// <param name="point1">Second point.</param>
        public PhysxWorldManifold2D(Vector2 normal, int pointCount, Vector2 point0, Vector2 point1)
        {
            Normal = normal;
            PointCount = pointCount;
            Point0 = point0;
            Point1 = point1;
        }

        /// <summary><see cref="Point0"/> for index 0, otherwise <see cref="Point1"/> (no range check).</summary>
        /// <param name="index">0 or 1.</param>
        public Vector2 this[int index] => index == 0 ? Point0 : Point1;

        /// <summary>The average of the contact points (zero without points): <c>(p0 + p1) / PointCount</c>
        /// summed in point order.</summary>
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
    /// A live contact between two fixtures. The engine hands out views, not copies:
    /// <list type="bullet">
    /// <item>The contact passed to an <see cref="IPhysxContactListener2D"/> callback is one view
    /// reused for every callback (no allocation per contact); it is valid only inside that call.</item>
    /// <item>Each contact yielded by <see cref="IPhysxWorldEngine2D.Contacts"/> is a distinct view
    /// (safe to collect, e.g. with <c>ToList()</c>), valid until the world's contact list changes
    /// (the next step, removing or disabling a body or fixture).</item>
    /// </list>
    /// Using a view after that throws <see cref="InvalidOperationException"/>. To keep a contact,
    /// copy it with <see cref="ToInfo"/>.
    /// </summary>
    public interface IPhysxContact2D
    {
        /// <summary>The engine's first fixture (orientation is arbitrary; <see cref="ContactRouter2D"/> orders by tag).</summary>
        IPhysxFixture2D FixtureA { get; }

        /// <summary>The engine's second fixture.</summary>
        IPhysxFixture2D FixtureB { get; }

        /// <summary>Body of <see cref="FixtureA"/>.</summary>
        IPhysxBody2D BodyA { get; }

        /// <summary>Body of <see cref="FixtureB"/>.</summary>
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
        /// <summary>The contact's world-space normal (A → B) and points, computed on each call.</summary>
        PhysxWorldManifold2D GetWorldManifold();

        /// <summary>A copy of the contact's current state that stays valid after the view does.</summary>
        PhysxContactInfo2D ToInfo() =>
            new(FixtureA, FixtureB, IsTouching, IsEnabled, Restitution, Friction, GetWorldManifold());
    }

    /// <summary>A snapshot of a contact (<see cref="IPhysxContact2D.ToInfo"/>), safe to keep.</summary>
    public readonly struct PhysxContactInfo2D
    {
        /// <summary>The engine's first fixture.</summary>
        public IPhysxFixture2D FixtureA { get; }

        /// <summary>The engine's second fixture.</summary>
        public IPhysxFixture2D FixtureB { get; }

        /// <summary>Body of <see cref="FixtureA"/> (read live from the fixture).</summary>
        public IPhysxBody2D BodyA => FixtureA.Body;

        /// <summary>Body of <see cref="FixtureB"/> (read live from the fixture).</summary>
        public IPhysxBody2D BodyB => FixtureB.Body;

        /// <summary>Touching at snapshot time.</summary>
        public bool IsTouching { get; }

        /// <summary>Enabled at snapshot time.</summary>
        public bool IsEnabled { get; }

        /// <summary>Mixed (or overridden) restitution at snapshot time.</summary>
        public float Restitution { get; }

        /// <summary>Mixed (or overridden) friction at snapshot time.</summary>
        public float Friction { get; }
        /// <summary>World-space normal (A to B) and points at the time of the snapshot.</summary>
        public PhysxWorldManifold2D Manifold { get; }
        /// <summary><see cref="Manifold"/>'s point count.</summary>
        public int PointCount => Manifold.PointCount;

        /// <summary>Creates a snapshot (normally via <see cref="IPhysxContact2D.ToInfo"/>).</summary>
        /// <param name="fixtureA">First fixture.</param>
        /// <param name="fixtureB">Second fixture.</param>
        /// <param name="isTouching">Touching flag.</param>
        /// <param name="isEnabled">Enabled flag.</param>
        /// <param name="restitution">Restitution.</param>
        /// <param name="friction">Friction.</param>
        /// <param name="manifold">World manifold.</param>
        public PhysxContactInfo2D(
            IPhysxFixture2D fixtureA, IPhysxFixture2D fixtureB,
            bool isTouching, bool isEnabled, float restitution, float friction,
            PhysxWorldManifold2D manifold)
        {
            FixtureA = fixtureA;
            FixtureB = fixtureB;
            IsTouching = isTouching;
            IsEnabled = isEnabled;
            Restitution = restitution;
            Friction = friction;
            Manifold = manifold;
        }
    }

    /// <summary>
    /// Raw contact callbacks of a world, all on the stepping thread, inside <c>Step</c> (install with
    /// <see cref="Altruist.Physx.IPhysxWorldEngine2D.SetContactListener"/>; one per world). Every method has
    /// an empty default, so implement only what you need. The contact passed in is a reused view valid
    /// only during the call (<see cref="IPhysxContact2D.ToInfo"/> to keep it). Do not create or destroy
    /// bodies / fixtures from a callback (Box2D's world is locked during the step); record and apply
    /// after <c>Step</c>.
    /// <para>For dispatch by the types of the two fixtures' tags, use <see cref="ContactRouter2D"/>
    /// (an implementation of this interface) instead of hand-written <c>is</c> ladders.</para>
    /// </summary>
    public interface IPhysxContactListener2D
    {
        /// <summary>Two fixtures' shapes started touching (also for sensors).</summary>
        void BeginContact(IPhysxContact2D contact) { }

        /// <summary>Two fixtures stopped touching, or the contact was destroyed (body / fixture removed or disabled).</summary>
        void EndContact(IPhysxContact2D contact) { }
        /// <summary>After collision detection, before solving: may disable the contact or change its material.</summary>
        void PreSolve(IPhysxContact2D contact) { }
        /// <summary>After solving, with the largest normal impulse applied at the contact.</summary>
        void PostSolve(IPhysxContact2D contact, float normalImpulse) { }
    }

    /// <summary>
    /// Ray-cast callback, called per fixture hit in no particular order. Return -1 to ignore the
    /// fixture, 0 to stop, the hit's <c>fraction</c> to clip the ray to this hit (closest hit),
    /// or 1 to continue unclipped (all hits).
    /// </summary>
    public interface IPhysxRayCastCallback2D
    {
        /// <summary>Called per fixture the ray hits.</summary>
        /// <param name="fixture">The fixture hit.</param>
        /// <param name="point">World hit point.</param>
        /// <param name="normal">Unit surface normal at the hit.</param>
        /// <param name="fraction">Fraction (0..1) along the ray.</param>
        /// <returns>-1 ignore, 0 stop, <paramref name="fraction"/> clip, 1 continue.</returns>
        float OnHit(IPhysxFixture2D fixture, Vector2 point, Vector2 normal, float fraction);
    }
}
