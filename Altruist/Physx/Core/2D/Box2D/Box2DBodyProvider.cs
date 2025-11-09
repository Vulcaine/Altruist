// Box2DPhysxBodyApiProvider2D.cs
using System.Numerics;
using System.Runtime.InteropServices;
using Altruist.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;

namespace Altruist.Physx
{

    class Body2DAdapter : IPhysxBody2D
    {
        public string Id { get; }
        public PhysxBodyType Type { get; set; }
        public float Mass { get => (float)_body.Mass; set { /* Box2D mass from fixtures; ignore set */ } }
        public object? UserData { get => _body.UserData; set => _body.UserData = value; }

        public Vector2 Position
        {
            get => _body.GetPosition();
            set => _body.SetTransform(value, _body.GetAngle());
        }

        public float RotationZ
        {
            get => _body.GetAngle();
            set => _body.SetTransform(_body.GetPosition(), value);
        }

        public Vector2 LinearVelocity
        {
            get => _body.LinearVelocity;
            set => _body.SetLinearVelocity(value);
        }

        public float AngularVelocityZ
        {
            get => _body.AngularVelocity;
            set => _body.SetAngularVelocity(value);
        }

        internal Body Underlying => _body;

        private readonly Body _body;
        private readonly List<IPhysxCollider> _colliders = new();

        public Body2DAdapter(string id, Body body, PhysxBodyType type)
        {
            Id = id;
            _body = body;
            Type = type;
        }

        public void AddCollider(IPhysxCollider collider) => _colliders.Add(collider);

        public bool RemoveCollider(IPhysxCollider collider)
        {
            // If the concrete collider exposes the underlying Fixture, destroy it.
            var fixtureProp = collider.GetType().GetProperty(
                "Fixture",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (fixtureProp?.GetValue(collider) is Fixture fx)
                _body.DestroyFixture(fx);

            return _colliders.Remove(collider);
        }

        public ReadOnlySpan<IPhysxCollider> GetColliders()
            => CollectionsMarshal.AsSpan(_colliders);

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
    /// </summary>
    public sealed class Box2DPhysxBodyApiProvider2D : IPhysxBodyApiProvider2D
    {
        private readonly Box2DWorldEngine2D _engine;

        // Track created fixtures per high-level collider
        private readonly Dictionary<IPhysxCollider2D, Fixture> _fixtures = new();

        public Box2DPhysxBodyApiProvider2D(IPhysxWorldEngine2D engine)
        {
            _engine = engine as Box2DWorldEngine2D
                      ?? throw new InvalidOperationException("Engine must be a Box2D-backed engine.");
        }

        /// <summary>
        /// Attach a collider to a specific Box2D body by creating a Fixture on that body.
        /// Stores the created fixture so it can be removed later via <see cref="RemoveCollider"/>.
        /// </summary>
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
            body.DestroyFixture(fixture);
            _fixtures.Remove(collider);
        }

        public IPhysxBody2D CreateBody(PhysxBodyType type, float mass, Transform2D transform)
        {
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
                        var angle = t.Rotation.Radians;
                        var sin = MathF.Sin(angle);
                        var cos = MathF.Cos(angle);

                        var transformed = new Vector2[verts.Length];
                        for (int i = 0; i < verts.Length; i++)
                        {
                            var v = verts[i];
                            var x = v.X * cos - v.Y * sin;
                            var y = v.X * sin + v.Y * cos;
                            transformed[i] = new Vector2(x + offset.X, y + offset.Y);
                        }

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
