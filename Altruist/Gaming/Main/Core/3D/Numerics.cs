namespace Altruist.Gaming.ThreeD.Numerics
{
    public struct IntVector3
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        public IntVector3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Operators
        public static IntVector3 operator +(IntVector3 a, IntVector3 b)
            => new IntVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static IntVector3 operator -(IntVector3 a, IntVector3 b)
            => new IntVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static IntVector3 operator *(IntVector3 v, int scalar)
            => new IntVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);

        public static IntVector3 operator *(int scalar, IntVector3 v)
            => v * scalar;

        public static IntVector3 operator /(IntVector3 v, int scalar)
            => new IntVector3(v.X / scalar, v.Y / scalar, v.Z / scalar);

        public override string ToString() => $"({X}, {Y}, {Z})";
    }

    public struct ByteVector3
    {
        public byte X { get; set; }
        public byte Y { get; set; }
        public byte Z { get; set; }

        public ByteVector3(byte x, byte y, byte z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Operators (with simple byte arithmetic wrapping)
        public static ByteVector3 operator +(ByteVector3 a, ByteVector3 b)
            => new ByteVector3((byte)(a.X + b.X), (byte)(a.Y + b.Y), (byte)(a.Z + b.Z));

        public static ByteVector3 operator -(ByteVector3 a, ByteVector3 b)
            => new ByteVector3((byte)(a.X - b.X), (byte)(a.Y - b.Y), (byte)(a.Z - b.Z));

        public static ByteVector3 operator *(ByteVector3 v, int scalar)
            => new ByteVector3((byte)(v.X * scalar), (byte)(v.Y * scalar), (byte)(v.Z * scalar));

        public static ByteVector3 operator *(int scalar, ByteVector3 v)
            => v * scalar;

        public static ByteVector3 operator /(ByteVector3 v, int scalar)
            => new ByteVector3((byte)(v.X / scalar), (byte)(v.Y / scalar), (byte)(v.Z / scalar));

        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}