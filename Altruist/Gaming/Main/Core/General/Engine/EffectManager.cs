namespace Altruist.Engine;

/// <summary>
/// Applies <see cref="IEffect"/> objects on the engine. A thin convenience over
/// <see cref="IEngineCore.ScheduleEffect"/> with a fixed rate; call the engine directly when you need
/// a different rate or a plain delegate.
/// </summary>
public interface IEffectManager
{
    /// <summary>Starts stepping <paramref name="effect"/> until its <see cref="IEffect.ExpiresAtUtc"/>.</summary>
    /// <returns>The id for <see cref="RemoveEffect"/>.</returns>
    TaskIdentifier ApplyEffect(IEffect effect);
    /// <summary>Stops an effect early (no-op if it already ended).</summary>
    void RemoveEffect(TaskIdentifier id);
}

/// <summary>Default <see cref="IEffectManager"/> (registered when <c>altruist:game</c> exists): steps effects at
/// <c>CycleRate(30, CycleUnit.Seconds)</c>, i.e. about 30 times per second.</summary>
[Service(typeof(IEffectManager))]
[ConditionalOnConfig("altruist:game")]
public class EffectManager : IEffectManager
{
    private readonly IAltruistEngine _engine;

    /// <summary>Created by DI.</summary>
    public EffectManager(IAltruistEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc/>
    public TaskIdentifier ApplyEffect(IEffect effect)
    {
        var rate = new CycleRate(30, CycleUnit.Seconds);

        return _engine.ScheduleEffect(
            cycleRate: rate,
            expiresAtUtc: effect.ExpiresAtUtc,
            step: effect.Step
        );
    }

    /// <inheritdoc/>
    public void RemoveEffect(TaskIdentifier id)
    {
        _engine.CancelEffect(id);
    }
}
