namespace Altruist.Gaming;

/// <summary>Null-safe helpers for the optional <see cref="ILagCompensationService"/>.</summary>
public static class LagCompensationExtensions
{
    /// <summary>Runs <paramref name="action"/> rewound to <paramref name="tick"/> when lag compensation is
    /// enabled, or directly when <paramref name="lagCompensation"/> is null.</summary>
    public static void RewindOrRun(this ILagCompensationService? lagCompensation, long tick, Action action)
    {
        if (lagCompensation == null)
        {
            action();
            return;
        }

        lagCompensation.RewindWorld(tick, action);
    }

    /// <summary>Like <see cref="RewindOrRun(ILagCompensationService, long, Action)"/>, returning the action's result.</summary>
    public static T RewindOrRun<T>(this ILagCompensationService? lagCompensation, long tick, Func<T> action)
        => lagCompensation == null ? action() : lagCompensation.RewindWorld(tick, action);
}
