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
        public Transform2D Transform { get; set; } = Transform2D.Zero;

        internal Fixture Native = null!;

        public Box2DFixture2D(Body2DAdapter body, in PhysxFixtureDef2D def)
        {
            Body = body;
            UserData = def.UserData;
            Shape = def.Shape.Kind == PhysxShapeKind2D.Circle ? PhysxColliderShape2D.Circle2D
                : def.Shape.IsBox ? PhysxColliderShape2D.Box2D
                : PhysxColliderShape2D.Polygon2D;
            Vertices = def.Shape.Vertices.Count > 0 ? def.Shape.Vertices.ToArray() : null;
        }

        /// <summary>Wraps a fixture created elsewhere (the provider API) on first use.</summary>
        private Box2DFixture2D(Body2DAdapter body, Fixture native)
        {
            Body = body;
            Native = native;
            Shape = native.Shape is CircleShape ? PhysxColliderShape2D.Circle2D : PhysxColliderShape2D.Polygon2D;
        }

        internal static Box2DFixture2D Of(Fixture fixture)
        {
            if (fixture.UserData is Box2DFixture2D f) return f;
            var body = fixture.Body.UserData as Body2DAdapter ?? new Body2DAdapter(Guid.NewGuid().ToString("N"), fixture.Body, PhysxBodyType.Dynamic);
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
                return false;
            }
            return true;
        }

        internal static void RaiseCollisionEnter(Box2DContact2D contact, float impulse)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            if (a.OnCollisionEnter is null && b.OnCollisionEnter is null) return;
            var m = contact.GetWorldManifold();
            a.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, m.Midpoint, m.Normal, impulse));
            b.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, m.Midpoint, -m.Normal, impulse));
        }

        internal static void RaiseStay(Box2DContact2D contact)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            if (a._onCollisionStay is null && b._onCollisionStay is null) return;
            var m = contact.GetWorldManifold();
            var impulse = contact.NormalImpulse;
            a._onCollisionStay?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, m.Midpoint, m.Normal, impulse));
            b._onCollisionStay?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, m.Midpoint, -m.Normal, impulse));
        }

        internal static void RaiseEnd(Box2DContact2D contact)
        {
            if (!TryPair(contact, out var a, out var b)) return;
            if (a.OnTriggerExit is null && a.OnCollisionExit is null && b.OnTriggerExit is null && b.OnCollisionExit is null) return;
            if (a.IsTrigger || b.IsTrigger)
            {
                a.OnTriggerExit?.Invoke(a, b);
                b.OnTriggerExit?.Invoke(b, a);
                return;
            }
            a.OnCollisionExit?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, Vector2.Zero, Vector2.Zero, 0f));
            b.OnCollisionExit?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, Vector2.Zero, Vector2.Zero, 0f));
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
