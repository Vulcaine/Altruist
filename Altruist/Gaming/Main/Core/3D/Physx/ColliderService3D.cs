/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Persistence;

namespace Altruist.Gaming.ThreeD
{
    public readonly record struct Bounds3D(Vector3 Min, Vector3 Max)
    {
        public Vector3 Size => Max - Min;
        public Vector3 Center => (Min + Max) * 0.5f;
        public static Bounds3D FromCenterSize(Vector3 center, Vector3 size)
        {
            var half = size * 0.5f;
            return new(center - half, center + half);
        }
    }

    public interface IColliderService3D
    {
        Bounds3D Compute(SphereCollider3DModel c);
        Bounds3D Compute(BoxCollider3DModel c);
        Bounds3D Compute(CapsuleCollider3DModel c);
        Task<Bounds3D> ComputeBoundsAsync(IPrefabHandle<IPrefabModel> prefab);
    }

    // [Service(typeof(IColliderService3D))]
    // public sealed class ColliderService3D : IColliderService3D
    // {
    //     private static readonly Bounds3D kDefault = Bounds3D.FromCenterSize(Vector3.Zero, new(1, 1, 1));

    //     public async Task<Bounds3D> ComputeBoundsAsync(IPrefabModel prefab, CancellationToken ct = default)
    //     {
    //         var accMin = new Vector3(float.PositiveInfinity);
    //         var accMax = new Vector3(float.NegativeInfinity);
    //         var any = false;

    //         // Read prefab component metadata (computed at startup)
    //         var components = PrefabMetadataRegistry.GetComponents(prefab.GetType());

    //         foreach (var meta in components)
    //         {
    //             // Only interested in collider components
    //             if (!typeof(Collider3DModel).IsAssignableFrom(meta.ComponentType))
    //                 continue;

    //             // Get the IPrefabHandle<TCollider> from the prefab instance
    //             var handleObj = meta.Getter(prefab);
    //             if (handleObj is null)
    //                 continue;

    //             var loader = ColliderHandleLoader.GetLoader(meta.ComponentType);
    //             var collider = await loader(handleObj, ct);
    //             if (collider is null || collider.IsTrigger)
    //                 continue;

    //             Bounds3D b = collider switch
    //             {
    //                 SphereCollider3DModel s => Compute(s),
    //                 BoxCollider3DModel b3 => Compute(b3),
    //                 CapsuleCollider3DModel c => Compute(c),
    //                 _ => kDefault
    //             };

    //             accMin = Vector3.Min(accMin, b.Min);
    //             accMax = Vector3.Max(accMax, b.Max);
    //             any = true;
    //         }

    //         return any ? new Bounds3D(accMin, accMax) : kDefault;
    //     }

    //     public Bounds3D Compute(SphereCollider3DModel c)
    //     {
    //         var t = c.Transform;
    //         float r = c.Radius ?? t.Scale.X;

    //         // Position3D -> Vector3 (avoid IntVector3 + Vector3 ops)
    //         var center = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);

    //         var rvec = new Vector3(r, r, r);
    //         return new Bounds3D(center - rvec, center + rvec);
    //     }

    //     public Bounds3D Compute(BoxCollider3DModel c)
    //     {
    //         var t = c.Transform;

    //         Vector3 he = c.HalfExtents ?? t.Scale.ToVector3(); // half-extents

    //         // Position3D -> Vector3
    //         var center = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);

    //         var R = Matrix3x3(t.Rotation.ToQuaternion());
    //         var absR = Abs(R);
    //         var e = Mul(absR, he);
    //         return new Bounds3D(center - e, center + e);
    //     }

    //     public Bounds3D Compute(CapsuleCollider3DModel c)
    //     {
    //         var t = c.Transform;
    //         float r = c.Radius ?? t.Scale.X;
    //         float h = c.HalfLength ?? t.Scale.Y;
    //         var axis = c.Axis ?? new Vector3(0, 1, 0);
    //         if (axis == Vector3.Zero)
    //             axis = new Vector3(0, 1, 0);
    //         axis = Vector3.Normalize(axis);

    //         var axisW = Vector3.Transform(axis, t.Rotation.ToQuaternion());
    //         var seg = axisW * h;

    //         // Position3D -> Vector3
    //         var pos = new Vector3(t.Position.X, t.Position.Y, t.Position.Z);

    //         var a = pos + seg;
    //         var b = pos - seg;
    //         var rvec = new Vector3(r, r, r);
    //         return new Bounds3D(Vector3.Min(a, b) - rvec, Vector3.Max(a, b) + rvec);
    //     }

    //     private static (Vector3 X, Vector3 Y, Vector3 Z) Matrix3x3(System.Numerics.Quaternion q)
    //     {
    //         q = System.Numerics.Quaternion.Normalize(q);
    //         float xx = q.X + q.X, yy = q.Y + q.Y, zz = q.Z + q.Z;
    //         float xy = q.X * yy, xz = q.X * zz, yz = q.Y * zz;
    //         float wx = q.W * xx, wy = q.W * yy, wz = q.W * zz;
    //         float xx2 = q.X * xx, yy2 = q.Y * yy, zz2 = q.Z * zz;

    //         var x = new Vector3(1 - (yy2 + zz2), xy + wz, xz - wy);
    //         var y = new Vector3(xy - wz, 1 - (xx2 + zz2), yz + wx);
    //         var z = new Vector3(xz + wy, yz - wx, 1 - (xx2 + yy2));
    //         return (x, y, z);
    //     }

    //     private static (Vector3 X, Vector3 Y, Vector3 Z) Abs((Vector3 X, Vector3 Y, Vector3 Z) m)
    //         => (Abs(m.X), Abs(m.Y), Abs(m.Z));

    //     private static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));

    //     private static Vector3 Mul((Vector3 X, Vector3 Y, Vector3 Z) m, Vector3 v)
    //         => new(Vector3.Dot(Abs(m.X), v), Vector3.Dot(Abs(m.Y), v), Vector3.Dot(Abs(m.Z), v));
    // }
}
