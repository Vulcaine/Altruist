// ColliderApi2D.cs

using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// Description of a primitive collider for <see cref="IPhysxColliderApiProvider2D.CreateCollider"/>.
    /// Size conventions (read when the collider is attached): <see cref="PhysxColliderShape2D.Circle2D"/>
    /// radius = <c>Transform.Size.X</c>; <see cref="PhysxColliderShape2D.Box2D"/> half extents =
    /// <c>Transform.Size</c>; <see cref="PhysxColliderShape2D.Capsule2D"/> radius = <c>Size.X</c>,
    /// half length of the straight segment (along local X) = <c>Size.Y</c>;
    /// <see cref="PhysxColliderShape2D.Polygon2D"/> uses <see cref="Vertices"/>.
    /// <c>Transform.Position</c> / <c>Rotation</c> are the offset in the body.
    /// </summary>
    public readonly struct PhysxCollider2DParams
    {
        /// <summary>Shape kind (<see cref="PhysxColliderShape2D.Chain2D"/> and
        /// <see cref="PhysxColliderShape2D.Edge2D"/> are engine fixtures only).</summary>
        public PhysxColliderShape2D Shape { get; }

        /// <summary>Local offset, rotation and size (see the type summary for the size convention).</summary>
        public Transform2D Transform { get; }

        /// <summary>Sensor: reports overlaps, no collision response.</summary>
        public bool IsTrigger { get; }

        /// <summary>Convex polygon vertices in the collider's local space (before <see cref="Transform"/>);
        /// empty for the other shapes.</summary>
        public ReadOnlyMemory<Vector2> Vertices { get; }

        /// <summary>Creates the description.</summary>
        /// <param name="shape">Shape kind.</param>
        /// <param name="transform">Local offset, rotation and size.</param>
        /// <param name="isTrigger">Sensor flag.</param>
        /// <param name="vertices">Polygon vertices (polygons only).</param>
        public PhysxCollider2DParams(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger,
            ReadOnlyMemory<Vector2> vertices = default)
        {
            Shape = shape;
            Transform = transform;
            IsTrigger = isTrigger;
            Vertices = vertices;
        }
    }

    /// <summary>
    /// Creates detached collider descriptions for the world-organizer path; attach them with
    /// <see cref="IPhysxBodyApiProvider2D.AddCollider"/>. DI service (the Box2D implementation is
    /// registered when <c>altruist:environment:mode</c> is <c>2D</c>); the static
    /// <see cref="PhysxCollider2D"/> shortcuts create the same <see cref="Collider2D"/> without DI. For standalone
    /// worlds use <see cref="Altruist.Physx.IPhysxWorldEngine2D.CreateFixture"/> with a <see cref="PhysxFixtureDef2D"/>
    /// instead (material, filter, chains and edges).
    /// </summary>
    public interface IPhysxColliderApiProvider2D
    {
        /// <summary>Creates a collider (not yet attached to any body).</summary>
        /// <param name="p">Shape, local transform, trigger flag and polygon vertices.</param>
        IPhysxCollider2D CreateCollider(in PhysxCollider2DParams p);
    }

    /// <summary>
    /// A collider description that becomes one or more engine fixtures when attached to a body with
    /// <see cref="IPhysxBodyApiProvider2D.AddCollider"/> (a capsule is a box plus two circles; every other
    /// shape is one fixture). Its events report the collider as a whole: enter and exit once per other
    /// collider however many of its fixtures touch it, stay at most once per other collider and step (with
    /// the geometry of one touching fixture pair), and the other side is the other
    /// <see cref="Collider2D"/> when it has one, otherwise the other fixture.
    /// <para>The fixtures get the collider's <see cref="IsTrigger"/> and <see cref="UserData"/> (the
    /// <see cref="ContactRouter2D"/> tag; changes while attached are copied to them), density 1 on dynamic
    /// bodies (0 otherwise), and Box2D's default friction (0.2), restitution (0) and filter. For a material or
    /// filter use <see cref="Altruist.Physx.IPhysxWorldEngine2D.CreateFixture"/> instead.</para>
    /// <para>The shape and <see cref="Transform"/> are fixed once attached (setting the transform then
    /// throws); remove the collider and attach a new one to change them. Not thread-safe.</para>
    /// </summary>
    public sealed class Collider2D : IPhysxCollider2D
    {
        private readonly Vector2[]? _vertices;
        private Transform2D _transform;
        private bool _isTrigger;
        private object? _userData;

        private Box2DFixture2D[] _fixtures = Array.Empty<Box2DFixture2D>();
        private readonly Dictionary<IPhysxCollider, int> _touching = new();
        private readonly Dictionary<IPhysxCollider, int> _overlapping = new();
        private readonly Dictionary<IPhysxCollider, int> _lastStayStep = new();

        private Action<IPhysxCollider, IPhysxCollider>? _onTriggerEnter;
        private Action<IPhysxCollider, IPhysxCollider>? _onTriggerExit;
        private Action<PhysxCollisionInfo2D>? _onCollisionEnter;
        private Action<PhysxCollisionInfo2D>? _onCollisionStay;
        private Action<PhysxCollisionInfo2D>? _onCollisionExit;

        /// <summary>Creates a detached collider from a description.</summary>
        /// <param name="p">Shape, local transform, trigger flag and polygon vertices.</param>
        /// <exception cref="ArgumentException">A polygon has fewer than 3 vertices, or the shape is a chain or an edge.</exception>
        public static Collider2D From(in PhysxCollider2DParams p)
        {
            if (p.Shape is PhysxColliderShape2D.Chain2D or PhysxColliderShape2D.Edge2D)
                throw new ArgumentException("Chains and edges are engine fixtures: create them with IPhysxWorldEngine2D.CreateFixture.", nameof(p));
            if (p.Shape == PhysxColliderShape2D.Polygon2D && p.Vertices.Length < 3)
                throw new ArgumentException("A polygon collider needs at least 3 vertices.", nameof(p));
            return new Collider2D(p);
        }

        private Collider2D(in PhysxCollider2DParams p)
        {
            Shape = p.Shape;
            _transform = p.Transform;
            _isTrigger = p.IsTrigger;
            _vertices = p.Shape == PhysxColliderShape2D.Polygon2D ? p.Vertices.ToArray() : null;
        }

        /// <inheritdoc/>
        public string Id { get; } = Guid.NewGuid().ToString("N");

        /// <inheritdoc/>
        public PhysxColliderShape2D Shape { get; }

        /// <summary>A copy of the polygon vertices (null for other shapes).</summary>
        public Vector2[]? Vertices => _vertices?.ToArray();

        /// <summary>The body this collider is attached to, or null while detached.</summary>
        public IPhysxBody2D? AttachedBody => _fixtures.Length > 0 ? _fixtures[0].Body : null;

        /// <summary>Local offset, rotation and size in the body.</summary>
        /// <exception cref="InvalidOperationException">Set while attached.</exception>
        public Transform2D Transform
        {
            get => _transform;
            set
            {
                if (_fixtures.Length > 0)
                    throw new InvalidOperationException("An attached collider's transform is fixed; remove it and attach a new one.");
                _transform = value;
            }
        }

        /// <summary>Sensor flag (copied to the fixtures while attached).</summary>
        public bool IsTrigger
        {
            get => _isTrigger;
            set
            {
                _isTrigger = value;
                foreach (var f in _fixtures) f.IsTrigger = value;
            }
        }

        /// <summary>Game data, also the fixtures' <see cref="ContactRouter2D"/> tag (copied to them while attached).</summary>
        public object? UserData
        {
            get => _userData;
            set
            {
                _userData = value;
                foreach (var f in _fixtures) f.UserData = value;
            }
        }

        /// <inheritdoc/>
        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter { add => _onTriggerEnter += value; remove => _onTriggerEnter -= value; }
        /// <inheritdoc/>
        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit { add => _onTriggerExit += value; remove => _onTriggerExit -= value; }
        /// <inheritdoc/>
        public event Action<PhysxCollisionInfo2D>? OnCollisionEnter { add => _onCollisionEnter += value; remove => _onCollisionEnter -= value; }
        /// <inheritdoc/>
        public event Action<PhysxCollisionInfo2D>? OnCollisionExit { add => _onCollisionExit += value; remove => _onCollisionExit -= value; }

        /// <inheritdoc/>
        public event Action<PhysxCollisionInfo2D>? OnCollisionStay
        {
            add
            {
                var had = _onCollisionStay is not null;
                _onCollisionStay += value;
                if (!had && _onCollisionStay is not null && Engine is { } e) e.StaySubscribers++;
            }
            remove
            {
                var had = _onCollisionStay is not null;
                _onCollisionStay -= value;
                if (had && _onCollisionStay is null && Engine is { } e) e.StaySubscribers--;
            }
        }

        internal bool IsAttached => _fixtures.Length > 0;

        internal IReadOnlyList<Box2DFixture2D> Fixtures => _fixtures;

        internal bool HasStaySubscribers => _onCollisionStay is not null;

        private Box2DWorldEngine2D? Engine => _fixtures.Length > 0 ? (_fixtures[0].Body as Body2DAdapter)?.Engine : null;

        internal void Attach(Box2DFixture2D[] fixtures)
        {
            _fixtures = fixtures;
            foreach (var f in fixtures) f.Owner = this;
            if (_onCollisionStay is not null && Engine is { } e) e.StaySubscribers++;
        }

        internal void Detach()
        {
            if (_onCollisionStay is not null && Engine is { } e) e.StaySubscribers--;
            foreach (var f in _fixtures) f.Owner = null;
            _fixtures = Array.Empty<Box2DFixture2D>();
            _touching.Clear();
            _overlapping.Clear();
            _lastStayStep.Clear();
        }

        internal void FixtureTriggerBegan(IPhysxCollider other)
        {
            if (Increment(_overlapping, other)) _onTriggerEnter?.Invoke(this, other);
        }

        internal void FixtureTriggerEnded(IPhysxCollider other)
        {
            if (Decrement(_overlapping, other)) _onTriggerExit?.Invoke(this, other);
        }

        internal void FixtureCollisionBegan(in PhysxCollisionInfo2D info)
        {
            if (Increment(_touching, info.OtherCollider)) _onCollisionEnter?.Invoke(info);
        }

        internal void FixtureCollisionEnded(in PhysxCollisionInfo2D info)
        {
            if (!Decrement(_touching, info.OtherCollider)) return;
            _lastStayStep.Remove(info.OtherCollider);
            _onCollisionExit?.Invoke(info);
        }

        internal void FixtureCollisionStayed(in PhysxCollisionInfo2D info, int step)
        {
            if (_onCollisionStay is null) return;
            if (_lastStayStep.TryGetValue(info.OtherCollider, out var last) && last == step) return;
            _lastStayStep[info.OtherCollider] = step;
            _onCollisionStay(info);
        }

        // True when the count went from 0 to 1.
        private static bool Increment(Dictionary<IPhysxCollider, int> counts, IPhysxCollider other)
        {
            counts.TryGetValue(other, out var n);
            counts[other] = n + 1;
            return n == 0;
        }

        // True when the count went from 1 to 0.
        private static bool Decrement(Dictionary<IPhysxCollider, int> counts, IPhysxCollider other)
        {
            if (!counts.TryGetValue(other, out var n)) return false;
            if (n > 1)
            {
                counts[other] = n - 1;
                return false;
            }
            counts.Remove(other);
            return true;
        }
    }

    /// <summary>
    /// Static shortcuts that create detached <see cref="Collider2D"/>s (no provider or DI needed); attach them
    /// with <see cref="IPhysxBodyApiProvider2D.AddCollider"/>. The same as
    /// <see cref="IPhysxColliderApiProvider2D.CreateCollider"/>.
    /// <example><code>
    /// var c = PhysxCollider2D.CreateCircle(0.5f);
    /// bodyProvider.AddCollider(body, c);
    /// </code></example>
    /// </summary>
    public static class PhysxCollider2D
    {
        /// <summary>Creates a collider from an explicit shape and transform (size in
        /// <c>transform.Size</c>, see <see cref="PhysxCollider2DParams"/>).</summary>
        /// <param name="shape">Shape kind (not a polygon, chain or edge).</param>
        /// <param name="transform">Local offset, rotation and size.</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static Collider2D Create(PhysxColliderShape2D shape, Transform2D transform, bool isTrigger = false)
            => Collider2D.From(new PhysxCollider2DParams(shape, transform, isTrigger));

        /// <summary>A circle of <paramref name="radius"/>; the transform's size is overwritten.</summary>
        /// <param name="radius">Radius in world units.</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static Collider2D CreateCircle(float radius, Transform2D? transform = null, bool isTrigger = false)
            => Create(PhysxColliderShape2D.Circle2D, (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, 0f)), isTrigger);

        /// <summary>A rectangle with the given half extents; the transform's size is overwritten.</summary>
        /// <param name="halfExtents">Half width (X) and half height (Y).</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static Collider2D CreateRectangle(Vector2 halfExtents, Transform2D? transform = null, bool isTrigger = false)
            => Create(PhysxColliderShape2D.Box2D, (transform ?? Transform2D.Zero).WithSize(Size2D.From(halfExtents)), isTrigger);

        /// <summary>A capsule: a straight segment of half length <paramref name="halfLength"/> along local X with
        /// round caps of <paramref name="radius"/> (attached as a box plus two circles).</summary>
        /// <param name="radius">Capsule radius.</param>
        /// <param name="halfLength">Half length of the straight segment (along local X).</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        public static Collider2D CreateCapsule(float radius, float halfLength, Transform2D? transform = null, bool isTrigger = false)
            => Create(PhysxColliderShape2D.Capsule2D, (transform ?? Transform2D.Zero).WithSize(Size2D.Of(radius, halfLength)), isTrigger);

        /// <summary>A convex polygon (3..8 vertices, copied).</summary>
        /// <param name="vertices">Local vertices.</param>
        /// <param name="transform">Local offset / rotation (default: none).</param>
        /// <param name="isTrigger">Sensor flag.</param>
        /// <exception cref="ArgumentException">Fewer than 3 vertices.</exception>
        public static Collider2D CreatePolygon(ReadOnlySpan<Vector2> vertices, Transform2D? transform = null, bool isTrigger = false)
            => Collider2D.From(new PhysxCollider2DParams(PhysxColliderShape2D.Polygon2D, transform ?? Transform2D.Zero, isTrigger, vertices.ToArray()));
    }
}
