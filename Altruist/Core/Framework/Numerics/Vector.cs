namespace Altruist.Numerics;

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