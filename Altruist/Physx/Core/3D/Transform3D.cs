using System.Numerics;

namespace Altruist.Physx.ThreeD.Numerics
{
    public readonly struct Position3D
    {
        private readonly Vector3 _v;
        public float X => _v.X;
        public float Y => _v.Y;
        public float Z => _v.Z;

        public Position3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Position3D(Vector3 v) => _v = v;

        public static Position3D Zero => new(Vector3.Zero);
        public static Position3D One => new(new Vector3(1f, 1f, 1f));
        public static Position3D Of(float x, float y, float z) => new(x, y, z);
        public static Position3D From(Vector3 v) => new(v);
        public Vector3 ToVector3() => _v;
    }

    public readonly struct Size3D
    {
        private readonly Vector3 _v;
        public float X => _v.X;
        public float Y => _v.Y;
        public float Z => _v.Z;

        public Size3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Size3D(Vector3 v) => _v = v;

        public static Size3D Zero => new(Vector3.Zero);
        public static Size3D One => new(new Vector3(1f, 1f, 1f));
        public static Size3D Of(float x, float y, float z) => new(x, y, z);
        public static Size3D From(Vector3 v) => new(v);
        public Vector3 ToVector3() => _v;
    }

    public readonly struct Scale3D
    {
        private readonly Vector3 _v;
        public float X => _v.X;
        public float Y => _v.Y;
        public float Z => _v.Z;

        public Scale3D(float x, float y, float z) => _v = new Vector3(x, y, z);
        private Scale3D(Vector3 v) => _v = v;

        public static Scale3D One => new(new Vector3(1f, 1f, 1f));
        public static Scale3D Zero => new(Vector3.Zero);
        public static Scale3D Of(float x, float y, float z) => new(x, y, z);
        public static Scale3D Uniform(float s) => new(new Vector3(s, s, s));
        public static Scale3D From(Vector3 v) => new(v);
        public Vector3 ToVector3() => _v;
    }

    public readonly struct Rotation3D
    {
        private readonly Quaternion _q;
        public Quaternion Value => _q;
        public Rotation3D(Quaternion q) { _q = q; }

        public static Rotation3D Identity => new(Quaternion.Identity);
        public static Rotation3D FromQuaternion(Quaternion q) => new(q);
        public static Rotation3D FromEulerRadians(float yaw, float pitch, float roll)
            => new(Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll));
        public static Rotation3D FromAxisAngle(Vector3 axis, float radians)
            => new(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians));

        public Quaternion ToQuaternion() => _q;
    }

    public readonly struct Transform3D
    {
        public Position3D Position { get; }
        public Size3D Size { get; }
        public Scale3D Scale { get; }
        public Rotation3D Rotation { get; }

        public Transform3D(Position3D position, Size3D size, Scale3D scale, Rotation3D rotation)
        {
            Position = position;
            Size = size;
            Scale = scale;
            Rotation = rotation;
        }

        public static Transform3D Zero => new(Position3D.Zero, Size3D.Zero, Scale3D.One, Rotation3D.Identity);

        public Transform3D WithPosition(Position3D p) => new(p, Size, Scale, Rotation);
        public Transform3D WithSize(Size3D s) => new(Position, s, Scale, Rotation);
        public Transform3D WithScale(Scale3D s) => new(Position, Size, s, Rotation);
        public Transform3D WithRotation(Rotation3D r) => new(Position, Size, Scale, rotation: r);
    }
}
