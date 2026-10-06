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
        public event Action<PhysxCollisionInfo2D>? OnCollisionEnter;
        public event Action<PhysxCollisionInfo2D>? OnCollisionStay;
        public event Action<PhysxCollisionInfo2D>? OnCollisionExit;

        private bool HasBeginSubscribers => OnTriggerEnter is not null || OnCollisionEnter is not null;
        private bool HasEndSubscribers => OnTriggerExit is not null || OnCollisionExit is not null;

        internal static void RaiseBegin(Box2DContact2D contact)
        {
            if (contact.Native!.FixtureA.UserData is not Box2DFixture2D a || contact.Native.FixtureB.UserData is not Box2DFixture2D b) return;
            if (!a.HasBeginSubscribers && !b.HasBeginSubscribers) return;
            if (a.IsTrigger || b.IsTrigger)
            {
                a.OnTriggerEnter?.Invoke(a, b);
                b.OnTriggerEnter?.Invoke(b, a);
                return;
            }
            var m = contact.GetWorldManifold();
            a.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(a, b, a.Body, b.Body, m.Midpoint, m.Normal, 0f));
            b.OnCollisionEnter?.Invoke(new PhysxCollisionInfo2D(b, a, b.Body, a.Body, m.Midpoint, -m.Normal, 0f));
        }

        internal static void RaiseEnd(Box2DContact2D contact)
        {
            if (contact.Native!.FixtureA.UserData is not Box2DFixture2D a || contact.Native.FixtureB.UserData is not Box2DFixture2D b) return;
            if (!a.HasEndSubscribers && !b.HasEndSubscribers) return;
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

    /// <summary>A reusable view over a native contact (valid only while <see cref="Native"/> is set).</summary>
    internal sealed class Box2DContact2D : IPhysxContact2D
    {
        internal Contact? Native;

        private Contact C => Native ?? throw new InvalidOperationException("The contact is only valid inside the callback or enumeration that handed it out.");

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
