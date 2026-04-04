namespace Altruist.Engine;

public interface IEffectManager
{
    TaskIdentifier ApplyEffect(IEffect effect);
    void RemoveEffect(TaskIdentifier id);
}

[Service(typeof(IEffectManager))]
[ConditionalOnConfig("altruist:game")]
public class EffectManager : IEffectManager
{
    private readonly IAltruistEngine _engine;

    public EffectManager(IAltruistEngine engine)
    {
        _engine = engine;
    }

    public TaskIdentifier ApplyEffect(IEffect effect)
    {
        var rate = new CycleRate(30, CycleUnit.Seconds);

        return _engine.ScheduleEffect(
            cycleRate: rate,
            expiresAtUtc: effect.ExpiresAtUtc,
            step: effect.Step
        );
    }

    public void RemoveEffect(TaskIdentifier id)
    {
        _engine.CancelEffect(id);
    }
}
