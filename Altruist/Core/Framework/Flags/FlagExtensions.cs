namespace Altruist;

/// <summary>
/// Allocation-light helpers for <c>[Flags]</c> enums: test, set and clear bits without casting to integers.
/// </summary>
/// <remarks>
/// Values are combined through their unsigned 64-bit representation. Negative values of signed underlying
/// types are not supported (conversion throws <see cref="OverflowException"/>). These helpers box the enum;
/// in very hot paths prefer the bitwise operators directly. Unlike <see cref="Enum.HasFlag(Enum)"/>,
/// <see cref="IsSet{TFlag}"/> treats a zero flag as "value is zero".
/// </remarks>
/// <example>
/// <code>
/// var f = Perm.Read.SetFlag(Perm.Write);   // Read | Write
/// bool canWrite = f.IsSet(Perm.Write);     // true
/// f = f.UnsetFlag(Perm.Read);              // Write
/// </code>
/// </example>
public static class FlagExtensions
{
    /// <summary>
    /// True when every bit of <paramref name="flag"/> is set in <paramref name="value"/>. A zero
    /// <paramref name="flag"/> (e.g. <c>None</c>) returns true only when <paramref name="value"/> is also zero.
    /// </summary>
    /// <typeparam name="TFlag">Flags enum type.</typeparam>
    /// <param name="value">Value to test.</param>
    /// <param name="flag">Bit or bit combination to look for.</param>
    public static bool IsSet<TFlag>(this TFlag value, TFlag flag)
        where TFlag : struct, Enum
    {
        ulong flagBits = ToUInt64(flag);
        ulong valueBits = ToUInt64(value);

        return flagBits == 0
            ? valueBits == 0
            : (valueBits & flagBits) == flagBits;
    }

    /// <summary>Returns <paramref name="value"/> with the bits of <paramref name="flag"/> added (bitwise OR). Pure; does not mutate.</summary>
    /// <typeparam name="TFlag">Flags enum type.</typeparam>
    /// <param name="value">Original value.</param>
    /// <param name="flag">Bits to set.</param>
    public static TFlag SetFlag<TFlag>(this TFlag value, TFlag flag)
        where TFlag : struct, Enum
    {
        return FromUInt64<TFlag>(ToUInt64(value) | ToUInt64(flag));
    }

    /// <summary>Returns <paramref name="value"/> with the bits of <paramref name="flag"/> cleared (AND NOT). Pure; does not mutate.</summary>
    /// <typeparam name="TFlag">Flags enum type.</typeparam>
    /// <param name="value">Original value.</param>
    /// <param name="flag">Bits to clear.</param>
    public static TFlag UnsetFlag<TFlag>(this TFlag value, TFlag flag)
        where TFlag : struct, Enum
    {
        return FromUInt64<TFlag>(ToUInt64(value) & ~ToUInt64(flag));
    }

    private static ulong ToUInt64<TFlag>(TFlag value)
        where TFlag : struct, Enum
    {
        return Convert.ToUInt64(value);
    }

    private static TFlag FromUInt64<TFlag>(ulong value)
        where TFlag : struct, Enum
    {
        return (TFlag)Enum.ToObject(typeof(TFlag), value);
    }
}
