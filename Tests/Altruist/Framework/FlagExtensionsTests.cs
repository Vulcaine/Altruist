using Altruist;

namespace Tests.Altruist.Framework;

public sealed class FlagExtensionsTests
{
    [Flags]
    private enum TestFlags : uint
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4,
    }

    [Fact]
    public void SetFlag_AddsFlag()
    {
        var flags = TestFlags.A.SetFlag(TestFlags.B);

        Assert.True(flags.IsSet(TestFlags.A));
        Assert.True(flags.IsSet(TestFlags.B));
        Assert.False(flags.IsSet(TestFlags.C));
    }

    [Fact]
    public void UnsetFlag_RemovesFlag()
    {
        var flags = (TestFlags.A | TestFlags.B).UnsetFlag(TestFlags.A);

        Assert.False(flags.IsSet(TestFlags.A));
        Assert.True(flags.IsSet(TestFlags.B));
    }

    [Fact]
    public void IsSet_RequiresAllBits()
    {
        var flags = TestFlags.A | TestFlags.B;

        Assert.True(flags.IsSet(TestFlags.A | TestFlags.B));
        Assert.False(flags.IsSet(TestFlags.A | TestFlags.C));
    }

    [Fact]
    public void IsSet_NoneMatchesOnlyNone()
    {
        Assert.True(TestFlags.None.IsSet(TestFlags.None));
        Assert.False(TestFlags.A.IsSet(TestFlags.None));
    }
}
