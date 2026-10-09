// Box2DPhysxBodyApiProvider2D.cs
using System.Numerics;
using System.Runtime.InteropServices;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;

namespace Altruist.Physx.TwoD
{

    /// <summary>
    /// <see cref="IPhysxBody2D"/> over a native Box2D body (whose <c>UserData</c> points back here).
    /// Created by <see cref="Box2DWorldEngine2D.CreateBody"/> or
    /// <see cref="Box2DPhysxBodyApiProvider2D.CreateBody"/>. All members read / write the native body
    /// directly (Box2D semantics: setting a non-zero velocity wakes the body; static bodies ignore
    /// velocity; position / angle setters call <c>SetTransform</c>). Not thread-safe.
    /// </summary>
    public class Body2DAdapter : IPhysxBody2D
    {
        /// <summary>Unique body id (from <see cref="PhysxBodyDef2D.Id"/> or a new GUID).</summary>
        public string Id { get; }

        /// <summary>The type the body was created with. Setting it only changes this value, not the
        /// native Box2D body type.</summary>
        public PhysxBodyType Type { get; set; }
        /// <summary>
        /// Box2D derives mass from the fixtures; setting it scales mass and rotational inertia so
        /// the body weighs exactly this much whatever its shape (set it after adding fixtures).
        /// </summary>
        public float Mass
        {
            get => (float)_body.Mass;
            set
            {
                _body.GetMassData(out var md);
                if (md.Mass <= 0) return;
                var k = value / md.Mass;
                md.Mass = value;
                md.RotationInertia *= k;
                _body.SetMassData(md);
            }
        }

        /// <inheritdoc/>
        public object? UserData { get; set; }

        /// <inheritdoc/>
        public bool IsAwake
        {
            get => _body.IsAwake;
            set => _body.IsAwake = value;
        }

        /// <summary>Box2D's enabled flag. Disabling destroys the body's contacts (end-contact fires)
        /// and invalidates contact views; enabling re-creates its broad-phase entries.</summary>
        public bool IsEnabled
        {
            get => _body.IsEnabled;
            set
            {
                // Disabling destroys the body's contacts.
                if (Engine is { } e) e.ContactGeneration++;
                _body.IsEnabled = value;
            }
        }

        /// <summary>Center of mass in world coordinates (Box2D <c>GetWorldCenter</c>).</summary>
        public Vector2 WorldCenter => _body.GetWorldCenter();

        /// <inheritdoc/>
        public void SetTransform(Vector2 position, float angle) => _body.SetTransform(position, angle);

        /// <inheritdoc/>
        public Vector2 GetWorldVector(Vector2 local) => _body.GetWorldVector(local);

        /// <inheritdoc/>
        public Vector2 GetLocalVector(Vector2 world) => _body.GetLocalVector(world);

        /// <inheritdoc/>
        public Vector2 GetWorldPoint(Vector2 local) => _body.GetWorldPoint(local);

        /// <inheritdoc/>
        public Vector2 GetLocalPoint(Vector2 world) => _body.GetLocalPoint(world);

        /// <summary>Optional layer tag (not used by the Box2D engine itself).</summary>
        public PhysxTag? PhysxTag { get; set; }

        /// <inheritdoc/>
        public Vector2 Position
        {
            get => _body.GetPosition();
            set => _body.SetTransform(value, _body.GetAngle());
        }

        /// <inheritdoc/>
        public float RotationZ
        {
            get => _body.GetAngle();
            set => _body.SetTransform(_body.GetPosition(), value);
        }

        /// <inheritdoc/>
        public Vector2 LinearVelocity
        {
            get => _body.LinearVelocity;
            set => _body.SetLinearVelocity(value);
        }

        /// <inheritdoc/>
        public float AngularVelocityZ
        {
            get => _body.AngularVelocity;
            set => _body.SetAngularVelocity(value);
        }

        internal Body Underlying => _body;

        /// <summary>The engine the body was added to (null until then).</summary>
        internal Box2DWorldEngine2D? Engine { get; set; }

        private readonly Body _body;
        private readonly List<IPhysxCollider> _colliders = new();

        /// <summary>Wraps a native body and sets its <c>UserData</c> to this adapter. Normally called by
        /// the engine / provider; the body must still be added to an engine to take part in queries.</summary>
        /// <param name="id">Body id.</param>
        /// <param name="body">The native Box2DSharp body.</param>
        /// <param name="type">The type to report (should match the native body).</param>
        public Body2DAdapter(string id, Body body, PhysxBodyType type)
        {
            Id = id;
            _body = body;
            Type = type;
            body.UserData = this;
        }

        /// <summary>Records <paramref name="collider"/> in this body's collider list only (does not
        /// create a native fixture; use <see cref="IPhysxWorldEngine2D.CreateFixture"/> or
        /// <see cref="Box2DPhysxBodyApiProvider2D.AddCollider"/> for that).</summary>
        public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);

        /// <summary>Removes <paramref name="collider"/> from the list and, when the collider exposes a
        /// Box2D <c>Fixture</c> property (looked up by reflection), destroys that fixture. Returns
        /// whether it was in the list.</summary>
        public bool RemoveCollider(IPhysxCollider collider)
        {
            // If the concrete collider exposes the underlying Fixture, destroy it.
            var fixtureProp = collider.GetType().GetProperty(
                "Fixture",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (fixtureProp?.GetValue(collider) is Fixture fx)
            {
                if (Engine is { } e) e.ContactGeneration++;
                _body.DestroyFixture(fx);
            }

            return _colliders.Remove(collider);
        }

        /// <summary>The colliders recorded with <see cref="AddCollider"/> (a view over the internal list;
        /// do not hold it across changes).</summary>
        public ReadOnlySpan<IPhysxCollider> GetColliders()
            => CollectionsMarshal.AsSpan(_colliders);

        /// <summary>Applies a 2D force kind at the center of mass (force / impulse / torque wake the body;
        /// set-velocity kinds are plain setters). Non-2D kinds are ignored.</summary>
        /// <param name="force">The force; 2D kinds read <c>Vector.X/Y</c>, torque and angular velocity read <c>Vector.Z</c>.</param>
        public void ApplyForce(in PhysxForce force)
        {
            switch (force.Type)
            {
                case PhysxForce.Kind.AddForce2D:
                    _body.ApplyForce(new Vector2(force.Vector.X, force.Vector.Y), _body.GetWorldCenter(), true);
                    break;
                case PhysxForce.Kind.AddImpulse2D:
                    _body.ApplyLinearImpulse(new Vector2(force.Vector.X, force.Vector.Y), _body.GetWorldCenter(), true);
                    break;
                case PhysxForce.Kind.AddTorque2D:
                    _body.ApplyTorque(force.Vector.Z, true);
                    break;
                case PhysxForce.Kind.SetLinearVelocity2D:
                    _body.SetLinearVelocity(new Vector2(force.Vector.X, force.Vector.Y));
                    break;
                case PhysxForce.Kind.SetAngularVelocity2D:
                    _body.SetAngularVelocity(force.Vector.Z);
                    break;
            }
        }

        /// <summary>Finds a recorded collider by its <c>Id</c> (ordinal comparison; read by reflection).</summary>
        /// <param name="colliderId">The id; null or empty never matches.</param>
        /// <param name="collider">The match, or <c>default</c>.</param>
        public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
        {
            if (string.IsNullOrEmpty(colliderId))
            {
                collider = default!;
                return false;
            }

            foreach (var c in _colliders)
            {
                // If the collider exposes an Id property, use it (like the 3D pattern).
                var idProp = c.GetType().GetProperty(
                    "Id",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (idProp != null && idProp.GetValue(c) is string id &&
                    string.Equals(id, colliderId, StringComparison.Ordinal))
                {
                    collider = c;
                    return true;
                }
            }

            collider = default!;
            return false;
        }

        /// <summary>The recorded collider at <paramref name="index"/>, or null when out of range.</summary>
        public IPhysxCollider? GetColliderAt(int index)
        {
            if ((uint)index < (uint)_colliders.Count)
                return _colliders[index];
            return null;
        }
    }

    /// <summary>
    /// Creates Box2D bodies inside the Box2D engine world and returns a Body2DAdapter.
    /// Caller must then register with the world via IPhysxWorld2D.AddBody(adapter).
    /// Also provides collider attach/detach helpers for Box2D-backed bodies.
    /// <para>DI: registered as <see cref="IPhysxBodyApiProvider2D"/> when
    /// <c>altruist:environment:mode</c> = <c>2D</c>. <see cref="SetEngine"/> must be called before
    /// <see cref="CreateBody"/>. Bound to one engine at a time; not thread-safe.</para>
    /// <para>Fixtures attached here get density 1 on dynamic bodies (else 0), default friction /
    /// restitution / filter, and no fixture user data, so <see cref="ContactRouter2D"/> sees the body's
    /// <see cref="IPhysxBody2D.UserData"/> as their tag. For full control use
    /// <see cref="IPhysxWorldEngine2D.CreateBody"/> / <see cref="IPhysxWorldEngine2D.CreateFixture"/>.</para>
    /// </summary>
    [Service(typeof(IPhysxBodyApiProvider2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public sealed class Box2DPhysxBodyApiProvider2D : IPhysxBodyApiProvider2D
    {
        private Box2DWorldEngine2D? _engine;

        // Track created fixtures per high-level collider
        private readonly Dictionary<IPhysxCollider2D, Fixture> _fixtures = new();

        /// <summary>Creates an unbound provider (call <see cref="SetEngine"/> before creating bodies).</summary>
        /// <param name="factory">Unused; accepted for DI.</param>
        public Box2DPhysxBodyApiProvider2D(IPhysxWorldEngineFactory2D? factory = null)
        {
            // Engine is set lazily when the first world is created
        }

        /// <summary>Bind to a specific engine instance (called by the organizer after world creation).</summary>
        /// <param name="engine">A <see cref="Box2DWorldEngine2D"/>.</param>
        /// <exception cref="InvalidOperationException"><paramref name="engine"/> is not Box2D-backed.</exception>
        public void SetEngine(IPhysxWorldEngine2D engine)
        {
            _engine = engine as Box2DWorldEngine2D
                      ?? throw new InvalidOperationException("Engine must be a Box2D-backed engine.");
        }

        /// <summary>
        /// Attach a collider to a specific Box2D body by creating a Fixture on that body.
        /// Stores the created fixture so it can be removed later via <see cref="RemoveCollider"/>.
        /// Shapes: circle radius = <c>Size.X</c>; box half extents = <c>Size</c>; capsule approximated as a
        /// box (half extents <c>Size.Y</c>, <c>Size.X</c>); polygon from <see cref="IPhysxCollider2D.Vertices"/>
        /// transformed by the collider's rotation and offset.
        /// </summary>
        /// <exception cref="InvalidOperationException">The body is not a <see cref="Body2DAdapter"/>, the collider
        /// is already attached, or a polygon has fewer than 3 vertices.</exception>
        /// <exception cref="NotSupportedException">Unknown shape kind.</exception>
        public void AddCollider(IPhysxBody2D body, IPhysxCollider2D collider)
        {
            if (body is not Body2DAdapter owner)
                throw new InvalidOperationException("Body must be a Box2D-backed Body2DAdapter.");

            if (_fixtures.ContainsKey(collider))
                throw new InvalidOperationException("This collider is already attached.");

            // Build a Box2D shape from collider data (no reflection)
            Shape shape = CreateB2ShapeFromCollider(collider);

            // Prepare fixture
            var fd = new FixtureDef
            {
                Shape = shape,
                IsSensor = collider.IsTrigger
            };

            // Density policy: dynamic -> 1.0, else 0.0
            fd.Density = body.Type == PhysxBodyType.Dynamic ? 1.0f : 0.0f;

            // Create fixture on body and cache it
            var fixture = owner.Underlying.CreateFixture(fd);
            _fixtures[collider] = fixture;

            // (filter/material hooks can be added here later)
        }

        /// <summary>
        /// Detach (destroy) the collider’s Box2D Fixture from whatever body it’s attached to.
        /// No-op if the collider is not currently attached.
        /// </summary>
        public void RemoveCollider(IPhysxCollider2D collider)
        {
            if (!_fixtures.TryGetValue(collider, out var fixture))
                return;

            var body = fixture.Body;
            if (_engine is not null) _engine.ContactGeneration++;
            body.DestroyFixture(fixture);
            _fixtures.Remove(collider);
        }

        /// <summary>Creates a native body in the bound engine's world at <paramref name="transform"/>'s
        /// position / rotation. The body is NOT added to the engine yet (call
        /// <see cref="IPhysxWorldEngine2D.AddBody"/>). <paramref name="mass"/> is ignored: Box2D derives mass
        /// from fixture densities (set <see cref="Body2DAdapter.Mass"/> after attaching colliders).</summary>
        /// <param name="type">Static, dynamic or kinematic.</param>
        /// <param name="mass">Ignored.</param>
        /// <param name="transform">Initial position and rotation.</param>
        /// <exception cref="InvalidOperationException"><see cref="SetEngine"/> was not called.</exception>
        public IPhysxBody2D CreateBody(PhysxBodyType type, float mass, Transform2D transform)
        {
            if (_engine == null)
                throw new InvalidOperationException("Box2D engine not set. Call SetEngine() first.");

            var bd = new BodyDef
            {
                Position = transform.Position.ToFloatVector2(),
                Angle = transform.Rotation.Radians,
                BodyType = type switch
                {
                    PhysxBodyType.Dynamic => BodyType.DynamicBody,
                    PhysxBodyType.Kinematic => BodyType.KinematicBody,
                    _ => BodyType.StaticBody
                }
            };

            var body = _engine.World.CreateBody(bd);
            var id = Guid.NewGuid().ToString("N");
            var adapter = new Body2DAdapter(id, body, type);

            // Box2D mass derives from fixtures (densities/areas). You can override via SetMassData if needed.
            return adapter;
        }

        // -------------------- helpers --------------------

        private static Shape CreateB2ShapeFromCollider(IPhysxCollider2D c)
        {
            var t = c.Transform;

            switch (c.Shape)
            {
                case PhysxColliderShape2D.Circle2D:
                    {
                        // Convention: Transform.Size.Width => radius
                        var radius = t.Size.X;
                        var center = t.Position.ToFloatVector2();
                        return new CircleShape { Radius = radius, Position = center };
                    }

                case PhysxColliderShape2D.Box2D:
                    {
                        // Convention: Transform.Size => half extents
                        var hx = t.Size.X;
                        var hy = t.Size.Y;
                        var center = t.Position.ToFloatVector2();
                        var angle = t.Rotation.Radians;
                        var poly = new PolygonShape();
                        poly.SetAsBox(hx, hy, center, angle);
                        return poly;
                    }

                case PhysxColliderShape2D.Capsule2D:
                    {
                        // Minimal approximation as oriented box: halfLength (X) and radius (Y)
                        var radius = t.Size.X;
                        var halfLen = t.Size.Y;
                        var hx = halfLen;
                        var hy = radius;
                        var center = t.Position.ToFloatVector2();
                        var angle = t.Rotation.Radians;
                        var poly = new PolygonShape();
                        poly.SetAsBox(hx, hy, center, angle);
                        return poly;
                    }

                case PhysxColliderShape2D.Polygon2D:
                    {
                        var verts = c.Vertices;
                        if (verts is null || verts.Length < 3)
                            throw new InvalidOperationException("Polygon collider requires Vertices with at least 3 points.");

                        // Apply local offset/rotation to vertices
                        var offset = t.Position.ToVector2();
                        var transformed = new Vector2[verts.Length];
                        for (int i = 0; i < verts.Length; i++)
                            transformed[i] = t.Rotation.Rotate(verts[i]) + new Vector2(offset.X, offset.Y);

                        var poly = new PolygonShape();
                        poly.Set(transformed);
                        return poly;
                    }

                default:
                    throw new NotSupportedException($"Unsupported collider shape: {c.Shape}");
            }
        }
    }
}
