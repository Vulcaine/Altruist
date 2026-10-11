using System.Numerics;

namespace Altruist.Numerics;

/// <summary>A mutable 3-component integer vector: grid cells, voxel/tile coordinates, integer origins.
/// For continuous positions use <see cref="System.Numerics.Vector3"/> (convert with <see cref="ToFloatVector3"/>).
/// Arithmetic is plain <c>int</c> arithmetic (unchecked overflow, truncating division).</summary>
public struct IntVector3
{
    /// <summary>X component.</summary>
    public int X { get; set; }
    /// <summary>Y component.</summary>
    public int Y { get; set; }
    /// <summary>Z component.</summary>
    public int Z { get; set; }

    /// <summary>Creates a vector from its components.</summary>
    public IntVector3(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Converts to a float <see cref="System.Numerics.Vector3"/> (exact for |component| &lt;= 2^24).</summary>
    public Vector3 ToFloatVector3()
    {
        return new Vector3(X, Y, Z);
    }

    // Operators
    /// <summary>Component-wise sum.</summary>
    public static IntVector3 operator +(IntVector3 a, IntVector3 b)
        => new IntVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Component-wise difference.</summary>
    public static IntVector3 operator -(IntVector3 a, IntVector3 b)
        => new IntVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Multiplies every component by <paramref name="scalar"/>.</summary>
    public static IntVector3 operator *(IntVector3 v, int scalar)
        => new IntVector3(v.X * scalar, v.Y * scalar, v.Z * scalar);

    /// <summary>Multiplies every component by <paramref name="scalar"/>.</summary>
    public static IntVector3 operator *(int scalar, IntVector3 v)
        => v * scalar;

    /// <summary>Divides every component by <paramref name="scalar"/> (integer division, truncates toward zero;
    /// throws <see cref="DivideByZeroException"/> for 0).</summary>
    public static IntVector3 operator /(IntVector3 v, int scalar)
        => new IntVector3(v.X / scalar, v.Y / scalar, v.Z / scalar);

    /// <summary>Formats as <c>(X, Y, Z)</c>.</summary>
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>A mutable 3-component byte vector (0..255 per component): compact sizes/counts where every
/// component is small. Every operator casts its result back to <c>byte</c>, so results wrap modulo 256
/// (e.g. 200 + 100 = 44) instead of saturating or throwing. Use <see cref="IntVector3"/> when values can
/// leave 0..255.</summary>
public struct ByteVector3
{
    /// <summary>X component.</summary>
    public byte X { get; set; }
    /// <summary>Y component.</summary>
    public byte Y { get; set; }
    /// <summary>Z component.</summary>
    public byte Z { get; set; }

    /// <summary>Creates a vector from its components.</summary>
    public ByteVector3(byte x, byte y, byte z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Operators (with simple byte arithmetic wrapping)
    /// <summary>Component-wise sum, wrapping modulo 256.</summary>
    public static ByteVector3 operator +(ByteVector3 a, ByteVector3 b)
        => new ByteVector3((byte)(a.X + b.X), (byte)(a.Y + b.Y), (byte)(a.Z + b.Z));

    /// <summary>Component-wise difference, wrapping modulo 256 (0 - 1 = 255).</summary>
    public static ByteVector3 operator -(ByteVector3 a, ByteVector3 b)
        => new ByteVector3((byte)(a.X - b.X), (byte)(a.Y - b.Y), (byte)(a.Z - b.Z));

    /// <summary>Multiplies every component by <paramref name="scalar"/>, wrapping modulo 256.</summary>
    public static ByteVector3 operator *(ByteVector3 v, int scalar)
        => new ByteVector3((byte)(v.X * scalar), (byte)(v.Y * scalar), (byte)(v.Z * scalar));

    /// <summary>Multiplies every component by <paramref name="scalar"/>, wrapping modulo 256.</summary>
    public static ByteVector3 operator *(int scalar, ByteVector3 v)
        => v * scalar;

    /// <summary>Divides every component by <paramref name="scalar"/> (integer division; a negative scalar
    /// wraps the result modulo 256; throws <see cref="DivideByZeroException"/> for 0).</summary>
    public static ByteVector3 operator /(ByteVector3 v, int scalar)
        => new ByteVector3((byte)(v.X / scalar), (byte)(v.Y / scalar), (byte)(v.Z / scalar));

    /// <summary>Formats as <c>(X, Y, Z)</c>.</summary>
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>A mutable 2-component integer vector: grid cells, tile/inventory coordinates, integer 2D
/// positions (backs <see cref="Altruist.TwoD.Numerics.Position2D"/>). For continuous values use
/// <see cref="System.Numerics.Vector2"/>. Plain <c>int</c> arithmetic (unchecked overflow, truncating
/// division).</summary>
public struct IntVector2
{
    /// <summary>X component.</summary>
    public int X { get; set; }
    /// <summary>Y component.</summary>
    public int Y { get; set; }

    /// <summary>Creates a vector from its components.</summary>
    public IntVector2(int x, int y)
    {
        X = x;
        Y = y;
    }

    // You can add operators, methods, etc. as needed
    /// <summary>Component-wise sum.</summary>
    public static IntVector2 operator +(IntVector2 a, IntVector2 b)
    {
        return new IntVector2(a.X + b.X, a.Y + b.Y);
    }

    /// <summary>Component-wise difference.</summary>
    public static IntVector2 operator -(IntVector2 a, IntVector2 b)
    {
        return new IntVector2(a.X - b.X, a.Y - b.Y);
    }

    /// <summary>Multiplies every component by <paramref name="scalar"/> (no <c>scalar * v</c> overload).</summary>
    public static IntVector2 operator *(IntVector2 v, int scalar)
    {
        return new IntVector2(v.X * scalar, v.Y * scalar);
    }

    /// <summary>Divides every component by <paramref name="scalar"/> (integer division, truncates toward zero;
    /// throws <see cref="DivideByZeroException"/> for 0).</summary>
    public static IntVector2 operator /(IntVector2 v, int scalar)
    {
        return new IntVector2(v.X / scalar, v.Y / scalar);
    }

    /// <summary>Formats as <c>(X, Y)</c>.</summary>
    public override string ToString()
    {
        return $"({X}, {Y})";
    }
}

/// <summary>A mutable 2-component byte vector (0..255 per component), e.g. a small item footprint in
/// grid cells. Every operator casts its result back to <c>byte</c>, so results wrap modulo 256 instead
/// of saturating or throwing. Use <see cref="IntVector2"/> when values can leave 0..255.</summary>
public struct ByteVector2
{
    /// <summary>X component.</summary>
    public byte X { get; set; }
    /// <summary>Y component.</summary>
    public byte Y { get; set; }

    /// <summary>Creates a vector from its components.</summary>
    public ByteVector2(byte x, byte y)
    {
        X = x;
        Y = y;
    }

    // Addition operator
    /// <summary>Component-wise sum, wrapping modulo 256.</summary>
    public static ByteVector2 operator +(ByteVector2 a, ByteVector2 b)
    {
        return new ByteVector2((byte)(a.X + b.X), (byte)(a.Y + b.Y));
    }

    // Subtraction operator
    /// <summary>Component-wise difference, wrapping modulo 256.</summary>
    public static ByteVector2 operator -(ByteVector2 a, ByteVector2 b)
    {
        return new ByteVector2((byte)(a.X - b.X), (byte)(a.Y - b.Y));
    }

    // Multiplication operator with scalar
    /// <summary>Multiplies every component by <paramref name="scalar"/>, wrapping modulo 256.</summary>
    public static ByteVector2 operator *(ByteVector2 v, int scalar)
    {
        return new ByteVector2((byte)(v.X * scalar), (byte)(v.Y * scalar));
    }

    // Division operator with scalar
    /// <summary>Divides every component by <paramref name="scalar"/> (integer division; throws
    /// <see cref="DivideByZeroException"/> for 0).</summary>
    public static ByteVector2 operator /(ByteVector2 v, int scalar)
    {
        return new ByteVector2((byte)(v.X / scalar), (byte)(v.Y / scalar));
    }

    // Override ToString for better string representation
    /// <summary>Formats as <c>(X, Y)</c>.</summary>
    public override string ToString()
    {
        return $"({X}, {Y})";
    }
}
