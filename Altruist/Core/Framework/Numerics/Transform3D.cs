using System.Numerics;

using Altruist.Numerics;

namespace Altruist.ThreeD.Numerics
{
    /// <summary>An immutable 3D world position (float). Altruist 3D convention: +Y is up, the ground is the
    /// XZ plane, and facing yaw 0 looks along +Z (see <see cref="Yaw3D"/>). Convert with
    /// <see cref="ToVector3"/> for <see cref="Vector3"/> math.</summary>
    public readonly struct Position3D
    {
        private readonly Vector3 _v;
        /// <summary>X coordinate (world units).</summary>
        public float X => _v.X;
        /// <summary>Y coordinate (world units, up).</summary>
        public float Y => _v.Y;
        /// <summary>Z coordinate (world units).</summary>
        public float Z => _v.Z;

        /// <summary>Creates a position from integer coordinates (converted to float).</summary>
        public Position3D(int x, int y, int z) => _v = new Vector3(x, y, z);
        /// <summary>Creates a position from float coordinates.</summary>
        public Position3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Position3D(Vector3 v) => _v = v;

        /// <summary>The origin (0, 0, 0).</summary>
        public static Position3D Zero => new(new Vector3(0, 0, 0));
        /// <summary>(1, 1, 1).</summary>
        public static Position3D One => new(new Vector3(1, 1, 1));
        /// <summary>Factory alias of the float constructor.</summary>
        public static Position3D Of(float x, float y, float z) => new(x, y, z);
        /// <summary>Converts an integer vector (e.g. a grid cell) to a position.</summary>
        public static Position3D From(IntVector3 v) => new(v.X, v.Y, (float)v.Z);
        /// <summary>Wraps an existing <see cref="Vector3"/>.</summary>
        public static Position3D From(Vector3 v) => new(v);
        /// <summary>The position as a <see cref="Vector3"/>.</summary>
        public Vector3 ToVector3() => _v;
    }

    /// <summary>An immutable 3D size in world units. Its meaning depends on the collider/body shape it
    /// describes: half extents for a box, <c>(radius, 0, 0)</c> for a sphere,
    /// <c>(radius, halfLength, 0)</c> for a capsule. Distinct from <see cref="Scale3D"/>, a unitless
    /// multiplier.</summary>
    public readonly struct Size3D
    {
        private readonly Vector3 _v;
        /// <summary>X extent (see the type summary for its per-shape meaning).</summary>
        public float X => _v.X;
        /// <summary>Y extent (see the type summary for its per-shape meaning).</summary>
        public float Y => _v.Y;
        /// <summary>Z extent (see the type summary for its per-shape meaning).</summary>
        public float Z => _v.Z;

        /// <summary>Creates a size from its three extents.</summary>
        public Size3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Size3D(Vector3 v) => _v = v;

        /// <summary>(0, 0, 0).</summary>
        public static Size3D Zero => new(Vector3.Zero);
        /// <summary>(1, 1, 1).</summary>
        public static Size3D One => new(new Vector3(1f, 1f, 1f));
        /// <summary>Factory alias of the constructor.</summary>
        public static Size3D Of(float x, float y, float z) => new(x, y, z);
        /// <summary>Wraps an existing <see cref="Vector3"/>.</summary>
        public static Size3D From(Vector3 v) => new(v);
        /// <summary>The size as a <see cref="Vector3"/>.</summary>
        public Vector3 ToVector3() => _v;
    }

    /// <summary>An immutable, unitless 3D scale factor (1 = unchanged). Use <see cref="One"/> as the
    /// neutral value, not <see cref="Zero"/>.</summary>
    public readonly struct Scale3D
    {
        private readonly Vector3 _v;
        /// <summary>X scale factor.</summary>
        public float X => _v.X;
        /// <summary>Y scale factor.</summary>
        public float Y => _v.Y;
        /// <summary>Z scale factor.</summary>
        public float Z => _v.Z;

        /// <summary>Creates a scale from per-axis factors.</summary>
        public Scale3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Scale3D(Vector3 v) => _v = v;

        /// <summary>(1, 1, 1): no scaling. The neutral value for <see cref="Transform3D"/>.</summary>
        public static Scale3D One => new(new Vector3(1f, 1f, 1f));
        /// <summary>(0, 0, 0): collapses everything; rarely what you want as a default.</summary>
        public static Scale3D Zero => new(Vector3.Zero);
        /// <summary>Factory alias of the constructor.</summary>
        public static Scale3D Of(float x, float y, float z) => new(x, y, z);
        /// <summary>The same factor <paramref name="s"/> on all three axes.</summary>
        public static Scale3D Uniform(float s) => new(new Vector3(s, s, s));
        /// <summary>Wraps an existing <see cref="Vector3"/>.</summary>
        public static Scale3D From(Vector3 v) => new(v);
        /// <summary>The scale as a <see cref="Vector3"/>.</summary>
        public Vector3 ToVector3() => _v;
    }

    /// <summary>An immutable 3D rotation backed by a <see cref="Quaternion"/> (System.Numerics conventions:
    /// right-handed, angles in radians). With Altruist's +Y-up / +Z-forward convention, a positive yaw
    /// turns +Z toward +X (<see cref="Yaw3D.ToDirection"/>); extract the facing yaw with
    /// <see cref="Yaw3D.Calculate"/>. The quaternion is stored as given (not normalized), except by
    /// <see cref="FromAxisAngle"/>, which normalizes the axis.</summary>
    public readonly struct Rotation3D
    {
        private readonly Quaternion _q;
        /// <summary>The underlying quaternion.</summary>
        public Quaternion Value => _q;
        /// <summary>Wraps a quaternion (expected to be unit length).</summary>
        public Rotation3D(Quaternion q) { _q = q; }

        /// <summary>No rotation (<see cref="Quaternion.Identity"/>).</summary>
        public static Rotation3D Identity => new(Quaternion.Identity);
        /// <summary>Wraps a quaternion (same as the constructor).</summary>
        public static Rotation3D FromQuaternion(Quaternion q) => new(q);
        /// <summary>From Euler angles in radians via <see cref="Quaternion.CreateFromYawPitchRoll"/>:
        /// <paramref name="yaw"/> about +Y, <paramref name="pitch"/> about +X, <paramref name="roll"/> about +Z
        /// (applied roll, then pitch, then yaw). For a pure heading use <see cref="FromYaw"/>.</summary>
        public static Rotation3D FromEulerRadians(float yaw, float pitch, float roll)
            => new(Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll));
        /// <summary>Rotation of <paramref name="radians"/> about <paramref name="axis"/> (normalized here, so
        /// any non-zero length works; a zero axis yields NaN).</summary>
        public static Rotation3D FromAxisAngle(Vector3 axis, float radians)
            => new(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));

        /// <summary>A pure heading: <paramref name="yawRadians"/> about +Y, in the same convention as
        /// <see cref="Yaw3D"/> (0 faces +Z, π/2 faces +X). Use this to face a direction on the ground plane;
        /// get the angle with <see cref="Yaw3D.FromDirection(float,float)"/>.</summary>
        public static Rotation3D FromYaw(float yawRadians)
            => new(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians));

        /// <summary>The underlying quaternion (same as <see cref="Value"/>).</summary>
        public Quaternion ToQuaternion() => _q;
    }

    /// <summary>A mutable 3D transform: <see cref="Position"/>, shape <see cref="Size"/>, unitless
    /// <see cref="Scale"/> and <see cref="Rotation"/>. Describes where a body/collider/world object sits
    /// and how big it is. Settable properties, plus <c>With*</c> methods that return copies.</summary>
    /// <example><code>
    /// var t = Transform3D.From(new Vector3(0, 1, 5), Rotation3D.FromYaw(MathF.PI / 2).Value, new Vector3(0.5f, 0.5f, 0.5f));
    /// t = t.WithPosition(Position3D.Of(0, 1, 6));
    /// </code></example>
    public struct Transform3D
    {
        /// <summary>World position.</summary>
        public Position3D Position { get; set; }
        /// <summary>Shape size (half extents / radius; see <see cref="Size3D"/>).</summary>
        public Size3D Size { get; set; }
        /// <summary>Unitless scale factor.</summary>
        public Scale3D Scale { get; set; }
        /// <summary>Rotation.</summary>
        public Rotation3D Rotation { get; set; }

        /// <summary>Formats the four parts. Note: the part structs do not override <c>ToString</c>, so they
        /// print as their type names.</summary>
        public override string ToString()
        {
            return $"(Position: {Position}, Size: {Size}, Scale: {Scale}, Rotation: {Rotation})";
        }

        /// <summary>Creates a transform from all four parts.</summary>
        public Transform3D(Position3D position, Size3D size, Scale3D scale, Rotation3D rotation)
        {
            Position = position;
            Size = size;
            Scale = scale;
            Rotation = rotation;
        }

        /// <summary>Builds a transform from raw System.Numerics values.</summary>
        public static Transform3D From(Vector3 origin, Quaternion rotation, Vector3 size, Vector3 scale)
        {
            return new Transform3D(
                Position3D.From(origin),
                Size3D.From(size),
                Scale3D.From(scale),
                Rotation3D.FromQuaternion(rotation));
        }

        /// <summary>Builds a transform from raw System.Numerics values with unit scale.</summary>
        public static Transform3D From(Vector3 origin, Quaternion rotation, Vector3 size)
        {
            return new Transform3D(
                Position3D.From(origin),
                Size3D.From(size),
                Scale3D.One,
                Rotation3D.FromQuaternion(rotation));
        }

        /// <summary>Builds a transform from an integer origin (e.g. a grid cell) with unit scale.</summary>
        public static Transform3D From(IntVector3 origin, Quaternion rotation, Vector3 size)
        {
            return new Transform3D(
                Position3D.From(origin),
                Size3D.From(size),
                Scale3D.One,
                Rotation3D.FromQuaternion(rotation));
        }

        /// <summary>Builds a transform from an integer origin (e.g. a grid cell) and an explicit scale.</summary>
        public static Transform3D From(IntVector3 origin, Quaternion rotation, Vector3 size, Vector3 scale)
        {
            return new Transform3D(
                Position3D.From(new IntVector3(origin.X, origin.Y, origin.Z)),
                Size3D.From(size),
                Scale3D.From(scale),
                Rotation3D.FromQuaternion(rotation));
        }

        /// <summary>Origin, zero size, unit scale, identity rotation. Use this as the "empty" transform.</summary>
        public static Transform3D Zero => new(Position3D.Zero, Size3D.Zero, Scale3D.One, Rotation3D.Identity);

        /// <summary>Position (1, 1, 1), unit size, unit scale, identity rotation. Note: despite the name the
        /// position is <see cref="Position3D.One"/>, not the origin; use <see cref="Zero"/> (or
        /// <see cref="WithPosition"/>) when the transform must sit at the origin.</summary>
        public static Transform3D Identity => new(Position3D.One, Size3D.One, Scale3D.One, Rotation3D.Identity);

        /// <summary>A copy with <paramref name="p"/> as position.</summary>
        public Transform3D WithPosition(Position3D p) => new(p, Size, Scale, Rotation);
        /// <summary>A copy with <paramref name="s"/> as size.</summary>
        public Transform3D WithSize(Size3D s) => new(Position, s, Scale, Rotation);
        /// <summary>A copy with <paramref name="s"/> as scale.</summary>
        public Transform3D WithScale(Scale3D s) => new(Position, Size, s, Rotation);
        /// <summary>A copy with <paramref name="r"/> as rotation.</summary>
        public Transform3D WithRotation(Rotation3D r) => new(Position, Size, Scale, rotation: r);
    }
}
