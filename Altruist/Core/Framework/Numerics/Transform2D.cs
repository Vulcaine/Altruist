// Transform2D.cs
using System.Numerics;
using Altruist.Numerics;

namespace Altruist.TwoD.Numerics
{
    public readonly struct Position2D
    {
        private readonly IntVector2 _v;
        public int X => _v.X;
        public int Y => _v.Y;

        public Position2D(int x, int y) => _v = new IntVector2(x, y);
        private Position2D(IntVector2 v) => _v = v;

        public static Position2D Zero => new(new IntVector2(0, 0));
        public static Position2D One => new(new IntVector2(1, 1));
        public static Position2D Of(int x, int y) => new(x, y);
        public static Position2D From(IntVector2 v) => new(v);
        public IntVector2 ToVector2() => _v;
        public Vector2 ToFloatVector2() => new(_v.X, _v.Y);
    }

    public readonly struct Size2D
    {
        private readonly Vector2 _v;
        public float X => _v.X;
        public float Y => _v.Y;

        public Size2D(float x, float y) => _v = new Vector2(x, y);
        private Size2D(Vector2 v) => _v = v;

        public static Size2D Zero => new(Vector2.Zero);
        public static Size2D One => new(new Vector2(1f, 1f));
        public static Size2D Of(float x, float y) => new(x, y);
        public static Size2D From(Vector2 v) => new(v);
        public Vector2 ToVector2() => _v;
    }

    public readonly struct Scale2D
    {
        private readonly Vector2 _v;
        public float X => _v.X;
        public float Y => _v.Y;

        public Scale2D(float x, float y) => _v = new Vector2(x, y);
        private Scale2D(Vector2 v) => _v = v;

        public static Scale2D One => new(new Vector2(1f, 1f));
        public static Scale2D Zero => new(Vector2.Zero);
        public static Scale2D Of(float x, float y) => new(x, y);
        public static Scale2D Uniform(float s) => new(new Vector2(s, s));
        public static Scale2D From(Vector2 v) => new(v);
        public Vector2 ToVector2() => _v;
    }

    public readonly struct Rotation2D
    {
        public float Radians { get; }
        public Rotation2D(float radians) { Radians = radians; }

        public static Rotation2D Zero => new(0f);
        public static Rotation2D FromRadians(float r) => new(r);
        public static Rotation2D FromDegrees(float deg) => new(MathF.PI * deg / 180f);
        public float ToDegrees() => Radians * 180f / MathF.PI;
    }

    public readonly struct Transform2D
    {
        public Position2D Position { get; }
        public Size2D Size { get; }
        public Scale2D Scale { get; }
        public Rotation2D Rotation { get; }

        public Transform2D(Position2D position, Size2D size, Scale2D scale, Rotation2D rotation)
        {
            Position = position;
            Size = size;
            Scale = scale;
            Rotation = rotation;
        }

        public static Transform2D Zero => new(Position2D.Zero, Size2D.Zero, Scale2D.One, Rotation2D.Zero);

        public Transform2D WithPosition(Position2D p) => new(p, Size, Scale, Rotation);
        public Transform2D WithSize(Size2D s) => new(Position, s, Scale, Rotation);
        public Transform2D WithScale(Scale2D s) => new(Position, Size, s, Rotation);
        public Transform2D WithRotation(Rotation2D r) => new(Position, Size, Scale, r);
    }
}
