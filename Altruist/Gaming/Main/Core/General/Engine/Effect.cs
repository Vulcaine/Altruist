namespace Altruist.Engine;

public interface IEffect
{
    DateTime ExpiresAtUtc { get; }
    void Step(float dt);
}

public sealed class DynamicEffectTask
{
    private static readonly CycleRate OneShotRate = new(1, CycleUnit.Ticks);

    public TaskIdentifier Id { get; }
    public CycleRate Rate { get; }
    public DateTime ExpiresAtUtc { get; }
    public long NextExecuteTimeTicks { get; set; }
    public Action<float> Step { get; }

    public long NextExecuteFrame { get; set; }

    /// <summary>Engine-clock deadline (stopwatch ticks) after which a repeating effect is removed.</summary>
    public long ExpiresAtTicks { get; set; } = long.MaxValue;

    /// <summary>The action of a one-shot timer (<c>ScheduleOnce</c> / <c>ScheduleAtFrame</c>); null for repeating effects.</summary>
    public Action? Once { get; private init; }

    public bool IsOneShot => Once is not null;

    /// <summary>Due by frame number (<see cref="NextExecuteFrame"/>) rather than by time (<see cref="NextExecuteTimeTicks"/>).</summary>
    public bool IsFrameBased { get; private init; }

    internal static DynamicEffectTask OneShot(TaskIdentifier id, Action action, bool atFrame = false) =>
        new(id, OneShotRate, DateTime.MaxValue, static _ => { }, 0)
        {
            Once = action,
            IsFrameBased = atFrame,
        };

    public DynamicEffectTask(TaskIdentifier id, CycleRate rate, DateTime expiresAtUtc, Action<float> step, long startTime)
    {
        Id = id;
        Rate = rate;
        ExpiresAtUtc = expiresAtUtc;
        Step = step;
        NextExecuteTimeTicks = startTime + rate.Value;
        IsFrameBased = rate.Unit == CycleUnit.Ticks;
    }
}
