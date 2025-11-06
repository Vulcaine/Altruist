namespace Altruist.Gaming.TwoD.Numerics
{

    public struct IntVector2
    {
        public int X { get; set; }
        public int Y { get; set; }

        public IntVector2(int x, int y)
        {
            X = x;
            Y = y;
        }

        // You can add operators, methods, etc. as needed
        public static IntVector2 operator +(IntVector2 a, IntVector2 b)
        {
            return new IntVector2(a.X + b.X, a.Y + b.Y);
        }

        public static IntVector2 operator -(IntVector2 a, IntVector2 b)
        {
            return new IntVector2(a.X - b.X, a.Y - b.Y);
        }

        public static IntVector2 operator *(IntVector2 v, int scalar)
        {
            return new IntVector2(v.X * scalar, v.Y * scalar);
        }

        public static IntVector2 operator /(IntVector2 v, int scalar)
        {
            return new IntVector2(v.X / scalar, v.Y / scalar);
        }

        public override string ToString()
        {
            return $"({X}, {Y})";
        }
    }

    public struct ByteVector2
    {
        public byte X { get; set; }
        public byte Y { get; set; }

        public ByteVector2(byte x, byte y)
        {
            X = x;
            Y = y;
        }

        // Addition operator
        public static ByteVector2 operator +(ByteVector2 a, ByteVector2 b)
        {
            return new ByteVector2((byte)(a.X + b.X), (byte)(a.Y + b.Y));
        }

        // Subtraction operator
        public static ByteVector2 operator -(ByteVector2 a, ByteVector2 b)
        {
            return new ByteVector2((byte)(a.X - b.X), (byte)(a.Y - b.Y));
        }

        // Multiplication operator with scalar
        public static ByteVector2 operator *(ByteVector2 v, int scalar)
        {
            return new ByteVector2((byte)(v.X * scalar), (byte)(v.Y * scalar));
        }

        // Division operator with scalar
        public static ByteVector2 operator /(ByteVector2 v, int scalar)
        {
            return new ByteVector2((byte)(v.X / scalar), (byte)(v.Y / scalar));
        }

        // Override ToString for better string representation
        public override string ToString()
        {
            return $"({X}, {Y})";
        }
    }
}