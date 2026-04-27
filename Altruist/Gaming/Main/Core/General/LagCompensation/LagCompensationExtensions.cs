namespace Altruist.Gaming;

public static class LagCompensationExtensions
{
    public static void RewindOrRun(this ILagCompensationService? lagCompensation, long tick, Action action)
    {
        if (lagCompensation == null)
        {
            action();
            return;
        }

        lagCompensation.RewindWorld(tick, action);
    }

    public static T RewindOrRun<T>(this ILagCompensationService? lagCompensation, long tick, Func<T> action)
        => lagCompensation == null ? action() : lagCompensation.RewindWorld(tick, action);
}
