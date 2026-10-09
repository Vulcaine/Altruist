/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.TwoD.Numerics;

using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Contacts;

namespace Altruist.Physx.TwoD
{
    /// <summary>A Box2D fixture behind <see cref="IPhysxFixture2D"/>; the native fixture's UserData points back here.</summary>
    internal sealed class Box2DFixture2D : IPhysxFixture2D
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IPhysxBody2D Body { get; }
        public object? UserData { get; set; }
        public PhysxColliderShape2D Shape { get; }
        public Vector2[]? Vertices { get; }
        /// <summary>The fixture's geometry as a local transform: a circle's center and radius, a box's
        /// center, half extents and angle; <see cref="Transform2D.Zero"/> for polygons, chains and edges
        /// (their vertices are already body-local). Fixed at creation.</summary>
        /// <exception cref="NotSupportedException">Set: a fixture's geometry cannot change; destroy it and create another.</exception>
        public Transform2D Transform
        {
            get => _transform;
            set => throw new NotSupportedException("A fixture's geometry is fixed; destroy it and create a new one.");
        }

        private readonly Transform2D _transform;

        internal Fixture Native = null!;

        /// <summary>The <see cref="Collider2D"/> this fixture is part of (attached through the body provider), or null.</summary>
        internal Collider2D? Owner;

        /// <summary>What other colliders see: the owning <see cref="Collider2D"/>, or this fixture.</summary>
        internal IPhysxCollider2D Facade => (IPhysxCollider2D?)Owner ?? this;

        public Box2DFixture2D(Body2DAdapter body, in PhysxFixtureDef2D def)
        {
            Body = body;
            UserData = def.UserData;
            var s = def.Shape;
            Shape = s.Kind switch
            {
                PhysxShapeKind2D.Circle => PhysxColliderShape2D.Circle2D,
                PhysxShapeKind2D.Chain => PhysxColliderShape2D.Chain2D,
                PhysxShapeKind2D.Edge => PhysxColliderShape2D.Edge2D,
                _ when s.IsBox => PhysxColliderShape2D.Box2D,
                _ => PhysxColliderShape2D.Polygon2D,
            };
            Vertices = s.Vertices.Count > 0 ? s.Vertices.ToArray() : null;
            _transform = s.Kind == PhysxShapeKind2D.Circle ? GeometryOf(s.Center, Size2D.Of(s.Radius, 0f), 0f)
                : s.IsBox ? GeometryOf(s.Center, Size2D.Of(s.HalfWidth, s.HalfHeight), s.BoxAngle)
                : Transform2D.Zero;
        }

        /// <summary>Wraps a native fixture created outside this engine's API on first use.</summary>
        private Box2DFixture2D(Body2DAdapter body, Fixture native)
        {
            Body = body;
            Native = native;
            Shape = native.Shape switch
            {
                CircleShape => PhysxColliderShape2D.Circle2D,
                ChainShape => PhysxColliderShape2D.Chain2D,
                EdgeShape => PhysxColliderShape2D.Edge2D,
                _ => PhysxColliderShape2D.Polygon2D,
            };
            _transform = native.Shape is CircleShape circle
                ? GeometryOf(circle.Position, Size2D.Of(circle.Radius, 0f), 0f)
                : Transform2D.Zero;
        }

        private static Transform2D GeometryOf(Vector2 center, Size2D size, float angle) =>
            new(Position2D.From(center), size, Scale2D.One, Rotation2D.FromRadians(angle));

        internal static Box2DFixture2D Of(Fixture fixture)
        {
            if (fixture.UserData is Box2DFixture2D f) return f;
            var body = fixture.Body.UserData as Body2DAdapter ?? new Body2DAdapter(Guid.NewGuid().ToString("N"), fixture.Body, Body2DAdapter.TypeOf(fixture.Body.BodyType));
            var wrapped = new Box2DFixture2D(body, fixture) { UserData = fixture.UserData };
            fixture.UserData = wrapped;
            return wrapped;
        }

        public bool IsTrigger
        {
            get => Native.IsSensor;
            set => Native.IsSensor = value;
        }

        public float Friction
        {
            get => Native.Friction;
            set => Native.Friction = value;
        }

        public float Restitution
        {
            get => Native.Restitution;
            set => Native.Restitution = value;
        }

        public float Density => Native.Density;

        public PhysxFilter2D Filter
        {
            get
            {
                var f = Native.Filter;
                return new PhysxFilter2D(f.CategoryBits, f.MaskBits, f.GroupIndex);
            }
            set => Native.Filter = new Filter { CategoryBits = value.Category, MaskBits = value.Mask, GroupIndex = value.Group };
        }

        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerEnter;
        public event Action<IPhysxCollider, IPhysxCollider>? OnTriggerExit;
        /// <summary>
        /// Raised after the step in which the fixtures started touching, with the total normal
        /// impulse the solver applied in that step (0 when they only came within the contact
        /// margin and did not push yet).
        /// </summary>
        public event Action<PhysxCollisionInfo2D>? OnCollisionEnter;
        public event Action<PhysxCollisionInfo2D>? OnCollisionExit;

        private Action<PhysxCollisionInfo2D>? _onCollisionStay;

        /// <summary>
        /// Raised after every later step while the fixtures touch (not for sensors, nor for
        /// contacts disabled in pre-solve), with the total normal impulse of that step's solve.
        /// </summary>
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

        // The world whose step raises this fixture's stays (counted there while subscribed).
        private Box2DWorldEngine2D? Engine => (Body as Body2DAdapter)?.Engine;

        private static bool TryPair(Box2DContact2D contact, out Box2DFixture2D a, out Box2DFixture2D b)
        {
            a = (contact.Native!.FixtureA.UserData as Box2DFixture2D)!;
            b = (contact.Native.FixtureB.UserData as Box2DFixture2D)!;
            return a is not null && b is not null;
        }

        /// <summary>
        /// Raises the trigger enters at once; returns true for a collision between two fixtures
        /// of the engine, whose enter is raised after the step (<see cref="RaiseCollisionEnter"/>).
        /// </summary>
        internal static bool RaiseBegin(Box2DContact2D contact)
        {
            if (!TryPair(contact, out var a, out var b)) return false;
            if (a.IsTrigger || b.IsTrigger)
            {
                a.OnTriggerEnter?.Invoke(a, b);
                b.OnTriggerEnter?.Invoke(b, a);
                a.Owner?.FixtureTriggerBegan(b.Facade);
                b.Owner?.FixtureTriggerBegan(a.Facade);
                return false;
            }
            return true;
        }

        internal static void RaiseCollisionEnter(Box2DContact2D contact, float impulse)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            if (a.OnCollisionEnter is null && b.OnCollisionEnter is null && a.Owner is null && b.Owner is null) return;
            var m = contact.GetWorldManifold();
            a.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, m.Midpoint, m.Normal, impulse));
            b.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, m.Midpoint, -m.Normal, impulse));
            a.Owner?.FixtureCollisionBegan(new PhysxCollisionInfo2D(a.Owner, b.Facade, a.Body, b.Body, m.Midpoint, m.Normal, impulse));
            b.Owner?.FixtureCollisionBegan(new PhysxCollisionInfo2D(b.Owner, a.Facade, b.Body, a.Body, m.Midpoint, -m.Normal, impulse));
        }

        internal static void RaiseStay(Box2DContact2D contact, int step)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            var aOwnerStays = a.Owner is { HasStaySubscribers: true };
            var bOwnerStays = b.Owner is { HasStaySubscribers: true };
            if (a._onCollisionStay is null && b._onCollisionStay is null && !aOwnerStays && !bOwnerStays) return;
            var m = contact.GetWorldManifold();
            var impulse = contact.NormalImpulse;
            a._onCollisionStay?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, m.Midpoint, m.Normal, impulse));
            b._onCollisionStay?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, m.Midpoint, -m.Normal, impulse));
            if (aOwnerStays) a.Owner!.FixtureCollisionStayed(new PhysxCollisionInfo2D(a.Owner, b.Facade, a.Body, b.Body, m.Midpoint, m.Normal, impulse), step);
            if (bOwnerStays) b.Owner!.FixtureCollisionStayed(new PhysxCollisionInfo2D(b.Owner, a.Facade, b.Body, a.Body, m.Midpoint, -m.Normal, impulse), step);
        }

        internal static void RaiseEnd(Box2DContact2D contact)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            if (a.OnTriggerExit is null && a.OnCollisionExit is null && b.OnTriggerExit is null && b.OnCollisionExit is null
                && a.Owner is null && b.Owner is null) return;
            if (a.IsTrigger || b.IsTrigger)
            {
                a.OnTriggerExit?.Invoke(a, b);
                b.OnTriggerExit?.Invoke(b, a);
                a.Owner?.FixtureTriggerEnded(b.Facade);
                b.Owner?.FixtureTriggerEnded(a.Facade);
                return;
            }
            a.OnCollisionExit?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, Vector2.Zero, Vector2.Zero, 0f));
            b.OnCollisionExit?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, Vector2.Zero, Vector2.Zero, 0f));
            a.Owner?.FixtureCollisionEnded(new PhysxCollisionInfo2D(a.Owner, b.Facade, a.Body, b.Body, Vector2.Zero, Vector2.Zero, 0f));
            b.Owner?.FixtureCollisionEnded(new PhysxCollisionInfo2D(b.Owner, a.Facade, b.Body, a.Body, Vector2.Zero, Vector2.Zero, 0f));
        }
    }

    /// <summary>
    /// A view over a native contact. The listener bridge reuses one view (valid only while
    /// <see cref="Native"/> is set, i.e. inside the callback); <c>Contacts</c> creates one per
    /// contact, bound to the world's contact generation (valid until the contact list may change).
    /// </summary>
    internal sealed class Box2DContact2D : IPhysxContact2D
    {
        internal Contact? Native;
        private readonly Box2DWorldEngine2D? _owner;
        private readonly int _generation;

        public Box2DContact2D() { }

        internal Box2DContact2D(Contact native, Box2DWorldEngine2D owner)
        {
            Native = native;
            _owner = owner;
            _generation = owner.ContactGeneration;
        }

        private Contact C => Native is { } c && (_owner is null || _owner.ContactGeneration == _generation)
            ? c
            : throw new InvalidOperationException(_owner is null
                ? "The contact is only valid inside the callback that handed it out; copy it with ToInfo() to keep it."
                : "The contact is no longer valid: the world stepped or a body was removed since it was enumerated; copy it with ToInfo() to keep it.");

        /// <summary>The total normal impulse stored at the contact's points by the last solve.</summary>
        internal float NormalImpulse
        {
            get
            {
                var c = C;
                var sum = 0f;
                for (var i = 0; i < c.Manifold.PointCount; i++) sum += c.Manifold.Points[i].NormalImpulse;
                return sum;
            }
        }

        public IPhysxFixture2D FixtureA => Box2DFixture2D.Of(C.FixtureA);
        public IPhysxFixture2D FixtureB => Box2DFixture2D.Of(C.FixtureB);
        public IPhysxBody2D BodyA => FixtureA.Body;
        public IPhysxBody2D BodyB => FixtureB.Body;
        public bool IsTouching => C.IsTouching;

        public bool IsEnabled
        {
            get => C.IsEnabled;
            set => C.SetEnabled(value);
        }

        public float Restitution
        {
            get => C.GetRestitution();
            set => C.SetRestitution(value);
        }

        public float Friction
        {
            get => C.GetFriction();
            set => C.SetFriction(value);
        }

        public int PointCount => C.Manifold.PointCount;

        public PhysxWorldManifold2D GetWorldManifold()
        {
            var c = C;
            var count = c.Manifold.PointCount;
            c.GetWorldManifold(out var wm);
            return new PhysxWorldManifold2D(wm.Normal, count, count > 0 ? wm.Points[0] : Vector2.Zero, count > 1 ? wm.Points[1] : Vector2.Zero);
        }
    }

    internal static class Box2DShapes
    {
        public static Shape Create(PhysxShape2D s)
        {
            switch (s.Kind)
            {
                case PhysxShapeKind2D.Circle:
                    return new CircleShape { Radius = s.Radius, Position = s.Center };
                case PhysxShapeKind2D.Polygon:
                    {
                        var poly = new PolygonShape();
                        if (s.IsBox && s.Center == Vector2.Zero && s.BoxAngle == 0f) poly.SetAsBox(s.HalfWidth, s.HalfHeight);
                        else if (s.IsBox) poly.SetAsBox(s.HalfWidth, s.HalfHeight, s.Center, s.BoxAngle);
                        else poly.Set(s.Vertices.ToArray());
                        return poly;
                    }
                case PhysxShapeKind2D.Chain:
                    {
                        var chain = new ChainShape();
                        var verts = s.Vertices.ToArray();
                        if (s.Loop) chain.CreateLoop(verts);
                        else chain.CreateChain(verts, verts.Length, verts[0], verts[^1]);
                        return chain;
                    }
                case PhysxShapeKind2D.Edge:
                    {
                        var edge = new EdgeShape();
                        var a = s.Vertices[0];
                        var b = s.Vertices[1];
                        edge.SetTwoSided(a, b);
                        return edge;
                    }
                default:
                    throw new NotSupportedException($"Unsupported shape: {s.Kind}");
            }
        }
    }
}
