namespace Altruist;

public static class FlagExtensions
{
    public static bool IsSet<TFlag>(this TFlag value, TFlag flag)
        where TFlag : struct, Enum
    {
        ulong flagBits = ToUInt64(flag);
        ulong valueBits = ToUInt64(value);

        return flagBits == 0
            ? valueBits == 0
            : (valueBits & flagBits) == flagBits;
    }

    public static TFlag SetFlag<TFlag>(this TFlag value, TFlag flag)
        where TFlag : struct, Enum
    {
        return FromUInt64<TFlag>(ToUInt64(value) | ToUInt64(flag));
    }

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
