namespace Altruist.Engine;

public interface IEffect
{
    DateTime ExpiresAtUtc { get; }
    void Step(float dt);
}

public sealed class DynamicEffectTask
{
    public TaskIdentifier Id { get; }
    public CycleRate Rate { get; }
    public DateTime ExpiresAtUtc { get; }
    public long NextExecuteTimeTicks { get; set; }
    public Action<float> Step { get; }

    public long NextExecuteFrame { get; set; }

    public DynamicEffectTask(TaskIdentifier id, CycleRate rate, DateTime expiresAtUtc, Action<float> step, long startTime)
    {
        Id = id;
        Rate = rate;
        ExpiresAtUtc = expiresAtUtc;
        Step = step;
        NextExecuteTimeTicks = startTime + rate.Value;
    }
}
