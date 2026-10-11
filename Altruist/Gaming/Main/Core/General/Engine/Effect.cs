namespace Altruist.Engine;

/// <summary>A timed effect applied through <see cref="IEffectManager.ApplyEffect"/> (a buff, a damage-over-time):
/// stepped on the engine loop until it expires.</summary>
public interface IEffect
{
    /// <summary>When the effect ends (UTC).</summary>
    DateTime ExpiresAtUtc { get; }
    /// <summary>Called on the engine loop each time the effect runs; <paramref name="dt"/> is the frame's delta in seconds.</summary>
    void Step(float dt);
}

/// <summary>The engine's bookkeeping record for an effect or one-shot timer
/// (<see cref="IEngineCore.ScheduleEffect"/>, <c>ScheduleOnce</c>, <c>ScheduleAtFrame</c>). Engine-internal; game
/// code holds the returned <see cref="TaskIdentifier"/> instead.</summary>
public sealed class DynamicEffectTask
{
    private static readonly CycleRate OneShotRate = new(1, CycleUnit.Ticks);

    /// <summary>The id returned to the scheduler's caller.</summary>
    public TaskIdentifier Id { get; }
    /// <summary>Repeat rate.</summary>
    public CycleRate Rate { get; }
    /// <summary>Requested wall-clock expiry (see <see cref="ExpiresAtTicks"/> for the enforced one).</summary>
    public DateTime ExpiresAtUtc { get; }
    /// <summary>Engine-clock timestamp of the next run (time-based effects).</summary>
    public long NextExecuteTimeTicks { get; set; }
    /// <summary>The repeating step (no-op for one-shots).</summary>
    public Action<float> Step { get; }

    /// <summary>Engine frame of the next run (frame-based effects).</summary>
    public long NextExecuteFrame { get; set; }

    /// <summary>Engine-clock deadline (stopwatch ticks) after which a repeating effect is removed.</summary>
    public long ExpiresAtTicks { get; set; } = long.MaxValue;

    /// <summary>The action of a one-shot timer (<c>ScheduleOnce</c> / <c>ScheduleAtFrame</c>); null for repeating effects.</summary>
    public Action? Once { get; private init; }

    /// <summary>True for one-shot timers.</summary>
    public bool IsOneShot => Once is not null;

    /// <summary>Due by frame number (<see cref="NextExecuteFrame"/>) rather than by time (<see cref="NextExecuteTimeTicks"/>).</summary>
    public bool IsFrameBased { get; private init; }

    internal static DynamicEffectTask OneShot(TaskIdentifier id, Action action, bool atFrame = false) =>
        new(id, OneShotRate, DateTime.MaxValue, static _ => { }, 0)
        {
            Once = action,
            IsFrameBased = atFrame,
        };

    /// <summary>Creates a repeating effect record; the engine overwrites the next-run fields when scheduling.</summary>
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
