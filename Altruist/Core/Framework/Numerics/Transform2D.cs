// Transform2D.cs
using System.Numerics;

using Altruist.Numerics;

namespace Altruist.TwoD.Numerics
{
    /// <summary>An immutable 2D world position (float), used by <see cref="Transform2D"/>; the 2D mirror of
    /// <see cref="Altruist.ThreeD.Numerics.Position3D"/>. Positions of physics bodies convert without loss
    /// (<see cref="From(Vector2)"/>); integer grid cells convert with <see cref="From(IntVector2)"/>. For
    /// continuous math use <see cref="ToVector2"/>.</summary>
    public readonly struct Position2D
    {
        private readonly Vector2 _v;
        /// <summary>X coordinate (world units).</summary>
        public float X => _v.X;
        /// <summary>Y coordinate (world units).</summary>
        public float Y => _v.Y;

        /// <summary>Creates a position from its coordinates (integers convert implicitly).</summary>
        public Position2D(float x, float y) => _v = new Vector2(x, y);
        private Position2D(Vector2 v) => _v = v;

        /// <summary>The origin (0, 0).</summary>
        public static Position2D Zero => new(Vector2.Zero);
        /// <summary>(1, 1).</summary>
        public static Position2D One => new(Vector2.One);
        /// <summary>Factory alias of the constructor: <c>Position2D.Of(x, y)</c>.</summary>
        public static Position2D Of(float x, float y) => new(x, y);
        /// <summary>Converts an integer vector (e.g. a grid cell) to a position.</summary>
        public static Position2D From(IntVector2 v) => new(v.X, v.Y);
        /// <summary>Wraps an existing <see cref="Vector2"/> (e.g. a body position) without rounding.</summary>
        public static Position2D From(Vector2 v) => new(v);
        /// <summary>The position as a <see cref="Vector2"/>.</summary>
        public Vector2 ToVector2() => _v;
        /// <summary>The same as <see cref="ToVector2"/> (what the <see cref="Distance2D"/>,
        /// <see cref="Direction2D"/> and <see cref="SpatialQueries2D"/> overloads use).</summary>
        public Vector2 ToFloatVector2() => _v;
        /// <summary>Formats the coordinates, e.g. <c>&lt;1.5, 2&gt;</c>.</summary>
        public override string ToString() => _v.ToString();
    }

    /// <summary>An immutable 2D size in world units. Its meaning depends on the collider/body shape it
    /// describes: half extents (half width, half height) for a box, <c>(radius, 0)</c> for a circle,
    /// <c>(radius, halfLength)</c> for a capsule. Distinct from <see cref="Scale2D"/>, which is a
    /// unitless multiplier.</summary>
    public readonly struct Size2D
    {
        private readonly Vector2 _v;
        /// <summary>X extent (see the type summary for its per-shape meaning).</summary>
        public float X => _v.X;
        /// <summary>Y extent (see the type summary for its per-shape meaning).</summary>
        public float Y => _v.Y;

        /// <summary>Creates a size from its two extents.</summary>
        public Size2D(float x, float y) => _v = new Vector2(x, y);
        private Size2D(Vector2 v) => _v = v;

        /// <summary>(0, 0).</summary>
        public static Size2D Zero => new(Vector2.Zero);
        /// <summary>(1, 1).</summary>
        public static Size2D One => new(new Vector2(1f, 1f));
        /// <summary>Factory alias of the constructor.</summary>
        public static Size2D Of(float x, float y) => new(x, y);
        /// <summary>Wraps an existing <see cref="Vector2"/>.</summary>
        public static Size2D From(Vector2 v) => new(v);
        /// <summary>The size as a <see cref="Vector2"/>.</summary>
        public Vector2 ToVector2() => _v;
    }

    /// <summary>An immutable, unitless 2D scale factor (1 = unchanged). Use <see cref="One"/> as the
    /// neutral value, not <see cref="Zero"/>.</summary>
    public readonly struct Scale2D
    {
        private readonly Vector2 _v;
        /// <summary>X scale factor.</summary>
        public float X => _v.X;
        /// <summary>Y scale factor.</summary>
        public float Y => _v.Y;

        /// <summary>Creates a scale from per-axis factors.</summary>
        public Scale2D(float x, float y) => _v = new Vector2(x, y);
        private Scale2D(Vector2 v) => _v = v;

        /// <summary>(1, 1): no scaling. The neutral value for <see cref="Transform2D"/>.</summary>
        public static Scale2D One => new(new Vector2(1f, 1f));
        /// <summary>(0, 0): collapses everything; rarely what you want as a default.</summary>
        public static Scale2D Zero => new(Vector2.Zero);
        /// <summary>Factory alias of the constructor.</summary>
        public static Scale2D Of(float x, float y) => new(x, y);
        /// <summary>The same factor <paramref name="s"/> on both axes.</summary>
        public static Scale2D Uniform(float s) => new(new Vector2(s, s));
        /// <summary>Wraps an existing <see cref="Vector2"/>.</summary>
        public static Scale2D From(Vector2 v) => new(v);
        /// <summary>The scale as a <see cref="Vector2"/>.</summary>
        public Vector2 ToVector2() => _v;
    }

    /// <summary>An immutable 2D rotation stored in radians, in the standard math / physics-body convention:
    /// 0 = no rotation (local +X stays along world +X), positive = counter-clockwise in a +Y-up frame
    /// (see <see cref="Rotate"/>). This is NOT the facing-yaw convention of <see cref="Yaw2D"/> and
    /// <see cref="Direction2D.TowardAngle"/> (0 = +Y, positive toward +X); for polar unit vectors in this
    /// convention use <see cref="Direction2D.FromPolar"/>. The angle is not normalized.</summary>
    public readonly struct Rotation2D
    {
        /// <summary>The angle in radians (counter-clockwise, unnormalized).</summary>
        public float Radians { get; }
        /// <summary>Creates a rotation from an angle in radians.</summary>
        public Rotation2D(float radians) { Radians = radians; }

        /// <summary>No rotation (0 rad).</summary>
        public static Rotation2D Zero => new(0f);
        /// <summary>Creates a rotation from radians.</summary>
        public static Rotation2D FromRadians(float r) => new(r);
        /// <summary>Creates a rotation from degrees (<c>π * deg / 180</c>).</summary>
        public static Rotation2D FromDegrees(float deg) => new(MathF.PI * deg / 180f);
        /// <summary>The angle in degrees (<c>Radians * 180 / π</c>).</summary>
        public float ToDegrees() => Radians * 180f / MathF.PI;

        /// <summary>Rotates <paramref name="v"/> by this angle (counter-clockwise): local to world.</summary>
        public Vector2 Rotate(Vector2 v)
        {
            var c = DeterministicMath.Cos(Radians);
            var s = DeterministicMath.Sin(Radians);
            return new Vector2(c * v.X - s * v.Y, s * v.X + c * v.Y);
        }

        /// <summary>The angle (radians, counter-clockwise as <see cref="Rotate"/>) at which a body's
        /// local +Y axis points along <paramref name="normal"/>: <c>DeterministicMath.Atan2(-normal.X, normal.Y)</c>.
        /// Aligning a body's "up" with a surface normal turns toward this angle.</summary>
        public static float AngleAligningUp(Vector2 normal) => DeterministicMath.Atan2(-normal.X, normal.Y);

        /// <summary>The angle at which a body facing <paramref name="facing"/> (+1: its front is local
        /// +X, otherwise local -X) points its front along <paramref name="direction"/> (aim the nose
        /// where the stick or the velocity points):
        /// <c>DeterministicMath.Atan2(direction.Y, direction.X) - (facing &gt; 0 ? 0 : MathF.PI)</c>.</summary>
        public static float AngleAligningForward(Vector2 direction, int facing) =>
            DeterministicMath.Atan2(direction.Y, direction.X) - (facing > 0 ? 0 : MathF.PI);

        /// <summary>The world direction of a body's local +Y at rotation <paramref name="radians"/>:
        /// <c>(-sin, cos)</c> (what <c>GetWorldVector((0, 1))</c> returns at that angle).</summary>
        public static Vector2 UpAt(float radians) => new(-DeterministicMath.Sin(radians), DeterministicMath.Cos(radians));

        /// <summary>The inverse of <see cref="Rotate"/>: world to local.</summary>
        public Vector2 Unrotate(Vector2 v)
        {
            var c = DeterministicMath.Cos(Radians);
            var s = DeterministicMath.Sin(Radians);
            return new Vector2(c * v.X + s * v.Y, -s * v.X + c * v.Y);
        }
    }

    /// <summary>An immutable 2D transform: <see cref="Position"/>, shape <see cref="Size"/>,
    /// unitless <see cref="Scale"/> and counter-clockwise <see cref="Rotation"/>. Used to describe where a
    /// body/collider/world object sits and how big it is. Modify with the <c>With*</c> methods, which return
    /// copies.</summary>
    /// <example><code>
    /// var t = new Transform2D(Position2D.Of(10, 4), Size2D.Of(0.5f, 0.5f), Scale2D.One, Rotation2D.FromDegrees(30));
    /// var moved = t.WithPosition(Position2D.Of(12, 4));
    /// </code></example>
    public readonly struct Transform2D
    {
        /// <summary>World position.</summary>
        public Position2D Position { get; }
        /// <summary>Shape size (half extents / radius; see <see cref="Size2D"/>).</summary>
        public Size2D Size { get; }
        /// <summary>Unitless scale factor.</summary>
        public Scale2D Scale { get; }
        /// <summary>Rotation (radians, counter-clockwise).</summary>
        public Rotation2D Rotation { get; }

        /// <summary>Creates a transform from all four parts.</summary>
        public Transform2D(Position2D position, Size2D size, Scale2D scale, Rotation2D rotation)
        {
            Position = position;
            Size = size;
            Scale = scale;
            Rotation = rotation;
        }

        /// <summary>Origin, zero size, unit scale (<see cref="Scale2D.One"/>), no rotation.</summary>
        public static Transform2D Zero => new(Position2D.Zero, Size2D.Zero, Scale2D.One, Rotation2D.Zero);

        /// <summary>A copy with <paramref name="p"/> as position.</summary>
        public Transform2D WithPosition(Position2D p) => new(p, Size, Scale, Rotation);
        /// <summary>A copy with <paramref name="s"/> as size.</summary>
        public Transform2D WithSize(Size2D s) => new(Position, s, Scale, Rotation);
        /// <summary>A copy with <paramref name="s"/> as scale.</summary>
        public Transform2D WithScale(Scale2D s) => new(Position, Size, s, Rotation);
        /// <summary>A copy with <paramref name="r"/> as rotation.</summary>
        public Transform2D WithRotation(Rotation2D r) => new(Position, Size, Scale, r);
    }
}
