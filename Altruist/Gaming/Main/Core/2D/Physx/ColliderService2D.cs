/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.TwoD
{
    public readonly record struct Bounds2D(Vector2 Min, Vector2 Max)
    {
        public Vector2 Size => Max - Min;
        public Vector2 Center => (Min + Max) * 0.5f;
        public static Bounds2D FromCenterSize(Vector2 center, Vector2 size)
        {
            var half = size * 0.5f;
            return new(center - half, center + half);
        }
    }

    public interface IColliderService2D
    {
        Bounds2D Compute(CircleCollider2DModel c);
        Bounds2D Compute(BoxCollider2DModel c);
        Bounds2D Compute(CapsuleCollider2DModel c);
        Bounds2D Compute(PolygonCollider2DModel c);
        Task<Bounds2D> ComputeBoundsAsync(IPrefabHandle<Prefab2D> prefab);
    }

    [Service(typeof(IColliderService2D))]
    public sealed class ColliderService2D : IColliderService2D
    {
        private static readonly Bounds2D kDefault = Bounds2D.FromCenterSize(Vector2.Zero, new(1, 1));

        public async Task<Bounds2D> ComputeBoundsAsync(IPrefabHandle<Prefab2D> prefab)
        {
            var accMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var accMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var any = false;

            foreach (var child in prefab.Children)
            {
                var tk = child.Type;
                if (tk != "engine.collider2d.circle" &&
                    tk != "engine.collider2d.box" &&
                    tk != "engine.collider2d.capsule" &&
                    tk != "engine.collider2d.polygon")
                    continue;

                var clr = VaultRegistry.GetClr(tk);
                var method = typeof(IPrefabHandle<Prefab2D>)
                    .GetMethod(nameof(IPrefabHandle<Prefab2D>.LoadChildAsync))!
                    .MakeGenericMethod(clr);
                var rowObj = await (Task<object?>)method.Invoke(prefab, new object[] { child })!;
                if (rowObj is not Collider2DModel base2d || base2d.IsTrigger)
                    continue;

                Bounds2D b = rowObj switch
                {
                    CircleCollider2DModel c => Compute(c),
                    BoxCollider2DModel c => Compute(c),
                    CapsuleCollider2DModel c => Compute(c),
                    PolygonCollider2DModel c => Compute(c),
                    _ => kDefault
                };

                accMin = Vector2.Min(accMin, b.Min);
                accMax = Vector2.Max(accMax, b.Max);
                any = true;
            }

            return any ? new Bounds2D(accMin, accMax) : kDefault;
        }

        public Bounds2D Compute(CircleCollider2DModel c)
        {
            var t = c.Transform;
            float r = c.Radius ?? t.Size.X;
            var center = t.Position.ToFloatVector2();
            var rvec = new Vector2(r, r);
            return new Bounds2D(center - rvec, center + rvec);
        }

        public Bounds2D Compute(BoxCollider2DModel c)
        {
            var t = c.Transform;
            Vector2 he = c.HalfExtents ?? t.Size.ToVector2();
            var center = t.Position.ToFloatVector2();

            if (t.Rotation.Radians == 0f)
                return new Bounds2D(center - he, center + he);

            float s = MathF.Sin(t.Rotation.Radians);
            float co = MathF.Cos(t.Rotation.Radians);
            var e = new Vector2(MathF.Abs(co) * he.X + MathF.Abs(s) * he.Y,
                                MathF.Abs(s) * he.X + MathF.Abs(co) * he.Y);
            return new Bounds2D(center - e, center + e);
        }

        public Bounds2D Compute(CapsuleCollider2DModel c)
        {
            var t = c.Transform;
            float r = c.Radius ?? t.Size.X;
            float h = c.HalfLength ?? t.Size.Y;

            var center = t.Position.ToFloatVector2();
            var aLocal = new Vector2(0, h);
            var bLocal = new Vector2(0, -h);

            float s = MathF.Sin(t.Rotation.Radians);
            float co = MathF.Cos(t.Rotation.Radians);
            static Vector2 Rot(Vector2 v, float co, float s) => new(co * v.X - s * v.Y, s * v.X + co * v.Y);

            var a = center + Rot(aLocal, co, s);
            var b = center + Rot(bLocal, co, s);
            var rr = new Vector2(r, r);
            return new Bounds2D(Vector2.Min(a, b) - rr, Vector2.Max(a, b) + rr);
        }

        public Bounds2D Compute(PolygonCollider2DModel c)
        {
            var t = c.Transform;
            float s = MathF.Sin(t.Rotation.Radians);
            float co = MathF.Cos(t.Rotation.Radians);
            static Vector2 Rot(Vector2 v, float co, float s) => new(co * v.X - s * v.Y, s * v.X + co * v.Y);

            if (c.Vertices == null || c.Vertices.Length == 0)
                return Bounds2D.FromCenterSize(t.Position.ToFloatVector2(), new(1, 1));

            var center = t.Position.ToFloatVector2();
            var size = t.Size.ToVector2();

            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

            foreach (var v in c.Vertices)
            {
                var scaled = new Vector2(v.X * size.X, v.Y * size.Y);
                var w = center + Rot(scaled, co, s);
                min = Vector2.Min(min, w);
                max = Vector2.Max(max, w);
            }
            return new Bounds2D(min, max);
        }
    }
}
