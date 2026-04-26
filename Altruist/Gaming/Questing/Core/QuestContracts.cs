namespace Altruist.Gaming.Questing;

public interface IQuestStateStore
{
    Task<Dictionary<string, QuestState>> LoadAsync(string subjectId);
    Task SaveDirtyAsync(string subjectId, IReadOnlyDictionary<string, QuestState> states);
    Task ResetAsync(string subjectId);
}

public interface IQuestUpdateSink<TContext> where TContext : QuestContext
{
    Task PublishAsync(TContext context, QuestUpdate update);
}

public sealed class NullQuestUpdateSink<TContext> : IQuestUpdateSink<TContext> where TContext : QuestContext
{
    public Task PublishAsync(TContext context, QuestUpdate update) => Task.CompletedTask;
}

public sealed class QuestRequirementContext<TContext> where TContext : QuestContext
{
    public required TContext QuestContext { get; init; }
    public required QuestRequirement Requirement { get; init; }
}

public interface IQuestRequirementEvaluator<TContext> where TContext : QuestContext
{
    string Key { get; }
    ValueTask<bool> EvaluateAsync(QuestRequirementContext<TContext> context);
}

public sealed class DelegateQuestRequirementEvaluator<TContext> : IQuestRequirementEvaluator<TContext>
    where TContext : QuestContext
{
    private readonly Func<QuestRequirementContext<TContext>, ValueTask<bool>> _evaluate;
    public string Key { get; }

    public DelegateQuestRequirementEvaluator(string key, Func<QuestRequirementContext<TContext>, ValueTask<bool>> evaluate)
    {
        Key = key;
        _evaluate = evaluate;
    }

    public ValueTask<bool> EvaluateAsync(QuestRequirementContext<TContext> context) => _evaluate(context);
}

public sealed class QuestRequirementRegistry<TContext> where TContext : QuestContext
{
    private readonly Dictionary<string, IQuestRequirementEvaluator<TContext>> _evaluators = new(StringComparer.OrdinalIgnoreCase);

    public QuestRequirementRegistry<TContext> Add(IQuestRequirementEvaluator<TContext> evaluator)
    {
        _evaluators[evaluator.Key] = evaluator;
        return this;
    }

    public QuestRequirementRegistry<TContext> Add(string key, Func<QuestRequirementContext<TContext>, bool> evaluator)
    {
        return Add(new DelegateQuestRequirementEvaluator<TContext>(key, ctx => ValueTask.FromResult(evaluator(ctx))));
    }

    public QuestRequirementRegistry<TContext> Add(string key, Func<QuestRequirementContext<TContext>, ValueTask<bool>> evaluator)
    {
        return Add(new DelegateQuestRequirementEvaluator<TContext>(key, evaluator));
    }

    public bool TryGet(string key, out IQuestRequirementEvaluator<TContext> evaluator) =>
        _evaluators.TryGetValue(key, out evaluator!);
}
