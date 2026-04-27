using System.Reflection;
using Altruist.Gaming.Questing;

namespace Tests.Gaming.Questing;

public sealed class TestQuestContext : QuestContext
{
    public List<string> Events { get; init; } = [];
}

[QuestHook("scroll")]
public interface IOnTestScroll : IQuestHook<TestQuestContext>
{
    Task OnScroll(TestQuestContext ctx);
}

public sealed class InMemoryQuestStateStore : IQuestStateStore
{
    public Dictionary<string, Dictionary<string, QuestState>> States { get; } = new(StringComparer.Ordinal);
    public int SaveCount { get; private set; }
    public int ResetCount { get; private set; }

    public Task<Dictionary<string, QuestState>> LoadAsync(string subjectId)
    {
        if (!States.TryGetValue(subjectId, out var states))
        {
            states = new Dictionary<string, QuestState>(StringComparer.Ordinal);
            States[subjectId] = states;
        }

        return Task.FromResult(states);
    }

    public Task SaveDirtyAsync(string subjectId, IReadOnlyDictionary<string, QuestState> states)
    {
        SaveCount++;
        States[subjectId] = states.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var state in States[subjectId].Values)
            state.MarkClean();
        return Task.CompletedTask;
    }

    public Task ResetAsync(string subjectId)
    {
        ResetCount++;
        States.Remove(subjectId);
        return Task.CompletedTask;
    }
}

public sealed class RecordingQuestUpdateSink : IQuestUpdateSink<TestQuestContext>
{
    public List<QuestUpdate> Updates { get; } = [];
    public List<string> HookKeys { get; } = [];

    public Task PublishAsync(TestQuestContext context, QuestUpdate update)
    {
        Updates.Add(update);
        HookKeys.Add(context.HookKey);
        return Task.CompletedTask;
    }
}

[Quest("test.enter")]
public sealed class EnterQuest : QuestBehavior<TestQuestContext>, IOnEnter<TestQuestContext>
{
    public Task OnEnter(TestQuestContext ctx)
    {
        ctx.Events.Add(ctx.HookKey);
        ctx.State.Increment("enter");
        return Task.CompletedTask;
    }
}

[Quest("test.kill")]
public sealed class KillQuest : QuestBehavior<TestQuestContext>, IOnKill<TestQuestContext>
{
    public Task OnKill(TestQuestContext ctx)
    {
        ctx.State.Set("killed", ctx.TargetId);
        return Task.CompletedTask;
    }
}

[Quest("test.custom")]
public sealed class CustomScrollQuest : QuestBehavior<TestQuestContext>, IOnTestScroll
{
    public Task OnScroll(TestQuestContext ctx)
    {
        ctx.Events.Add(ctx.HookKey);
        ctx.State.Set("scroll", true);
        return Task.CompletedTask;
    }
}

[Quest("test.requirement")]
[QuestRequirement("allow", QuestOperator.Equals, true)]
public sealed class RequirementQuest : QuestBehavior<TestQuestContext>, IOnEnter<TestQuestContext>
{
    public Task OnEnter(TestQuestContext ctx)
    {
        ctx.State.Set("ran", true);
        return Task.CompletedTask;
    }
}

[Quest("test.missing_requirement")]
[QuestRequirement("missing", QuestOperator.Equals, true)]
public sealed class MissingRequirementQuest : QuestBehavior<TestQuestContext>, IOnEnter<TestQuestContext>
{
    public Task OnEnter(TestQuestContext ctx)
    {
        ctx.State.Set("ran", true);
        return Task.CompletedTask;
    }
}

[Quest("test.npc")]
[QuestNpc("blacksmith")]
public sealed class NpcQuest : QuestBehavior<TestQuestContext>, IOnNpc<TestQuestContext>
{
    public Task OnNpc(TestQuestContext ctx)
    {
        ctx.State.Increment("npc");
        return Task.CompletedTask;
    }
}

[Quest("test.level")]
public sealed class LevelQuest : QuestBehavior<TestQuestContext>, IOnLevel<TestQuestContext>, IQuestLevelResetHook<TestQuestContext>
{
    public Task OnLevel(TestQuestContext ctx)
    {
        ctx.State.Set("last_level", ctx.Value);
        ctx.State.Increment("level_count");
        return Task.CompletedTask;
    }

    public void ResetAboveLevel(TestQuestContext ctx, int level)
    {
        ctx.State.Set("reset_to", level);
    }
}

[Quest("test.completed")]
public sealed class CompletedQuest : QuestBehavior<TestQuestContext>, IOnEnter<TestQuestContext>
{
    public Task OnEnter(TestQuestContext ctx)
    {
        ctx.State.Set("_done", true);
        return Task.CompletedTask;
    }
}

[Quest("test.state_machine")]
public sealed class StateMachineQuest : QuestBehavior<TestQuestContext>
{
    [QuestState("start", Initial = true)]
    private Task<string?> Start(TestQuestContext ctx)
    {
        ctx.State.Increment("start_ticks");
        return Task.FromResult<string?>("finish");
    }

    [QuestStateExit("start")]
    private Task ExitStart(TestQuestContext ctx)
    {
        ctx.State.Set("exited_start", true);
        return Task.CompletedTask;
    }

    [QuestStateEnter("finish")]
    private Task EnterFinish(TestQuestContext ctx)
    {
        ctx.State.Set("entered_finish", true);
        return Task.CompletedTask;
    }

    [QuestState("finish")]
    private Task<string?> Finish(TestQuestContext ctx)
    {
        ctx.State.Increment("finish_ticks");
        return Task.FromResult<string?>(null);
    }
}

public sealed class QuestRuntimeTests
{
    [Fact]
    public async Task FireQuestAsync_DispatchesOnlyImplementedBuiltInHook()
    {
        var fixture = CreateRuntime("subject-enter");

        bool fired = await fixture.Runtime.FireQuestAsync(fixture.Context, "test.enter", QuestTrigger.Enter);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.enter");
        Assert.True(fired);
        Assert.Equal(1, state.Get("enter", 0));
        Assert.Contains(QuestHooks.Enter, fixture.Context.Events);
    }

    [Fact]
    public async Task FireQuestHookAsync_DispatchesCustomHookInterface()
    {
        var fixture = CreateRuntime("subject-custom");

        bool fired = await fixture.Runtime.FireQuestHookAsync(fixture.Context, "test.custom", "scroll");

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.custom");
        Assert.True(fired);
        Assert.True(state.Get("scroll", false));
        Assert.Contains("scroll", fixture.Context.Events);
    }

    [Fact]
    public async Task RequirementFailure_SuspendsAndDoesNotRunHook()
    {
        var fixture = CreateRuntime("subject-denied", allowRequirement: false);

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.requirement", QuestTrigger.Enter);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.requirement");
        var update = fixture.Sink.Updates.Last(u => u.QuestId == "test.requirement");
        Assert.False(state.Get("ran", false));
        Assert.Equal(QuestStatus.Suspended, update.Status);
        Assert.Contains(update.Requirements, r => r.Key == "allow" && !r.Passed);
    }

    [Fact]
    public async Task RequirementSuccess_RunsHookAndPublishesActive()
    {
        var fixture = CreateRuntime("subject-allowed", allowRequirement: true);

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.requirement", QuestTrigger.Enter);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.requirement");
        var update = fixture.Sink.Updates.Last(u => u.QuestId == "test.requirement");
        Assert.True(state.Get("ran", false));
        Assert.Equal(QuestStatus.Active, update.Status);
        Assert.Contains(update.Requirements, r => r.Key == "allow" && r.Passed);
    }

    [Fact]
    public async Task MissingRequirementEvaluator_SuspendsQuest()
    {
        var fixture = CreateRuntime("subject-missing");

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.missing_requirement", QuestTrigger.Enter);

        var update = fixture.Sink.Updates.Last(u => u.QuestId == "test.missing_requirement");
        Assert.Equal(QuestStatus.Suspended, update.Status);
        Assert.Contains(update.Requirements, r => r.Key == "missing" && r.Reason == "missing_evaluator");
    }

    [Fact]
    public async Task NpcBinding_DispatchesOnlyMatchingNpcQuests()
    {
        var fixture = CreateRuntime("subject-npc");

        await fixture.Runtime.FireAsync(fixture.Context, QuestTrigger.Npc, npcKey: "blacksmith");

        var npcState = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.npc");
        var enterState = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.enter");
        Assert.Equal(1, npcState.Get("npc", 0));
        Assert.Equal(0, enterState.Get("enter", 0));
    }

    [Fact]
    public async Task ReconcileLevelAsync_BackfillsOnceAndIsIdempotent()
    {
        var fixture = CreateRuntime("subject-level");

        await fixture.Runtime.ReconcileLevelAsync(fixture.Context, previousLevel: 1, currentLevel: 3);
        await fixture.Runtime.ReconcileLevelAsync(fixture.Context, previousLevel: 3, currentLevel: 3);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.level");
        Assert.Equal(3, state.Get("last_level", 0));
        Assert.Equal(3, state.Get("level_count", 0));
    }

    [Fact]
    public async Task ReconcileLevelAsync_LevelDownCallsResetHookAndSuspendsHigherProgressMarker()
    {
        var fixture = CreateRuntime("subject-level-down");

        await fixture.Runtime.ReconcileLevelAsync(fixture.Context, previousLevel: 1, currentLevel: 5);
        await fixture.Runtime.ReconcileLevelAsync(fixture.Context, previousLevel: 5, currentLevel: 2);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.level");
        Assert.Equal(2, state.Get("reset_to", 0));
        Assert.Equal(2, state.Get("__level_reconciled", 0));
    }

    [Fact]
    public async Task CompletedState_PublishesCompletedStatus()
    {
        var fixture = CreateRuntime("subject-completed");

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.completed", QuestTrigger.Enter);

        var update = fixture.Sink.Updates.Last(u => u.QuestId == "test.completed");
        Assert.Equal(QuestStatus.Completed, update.Status);
    }

    [Fact]
    public async Task StateMachine_DispatchesInitialStateTransitionAndLifecycleHooks()
    {
        var fixture = CreateRuntime("subject-state-machine");

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.state_machine", QuestTrigger.Enter);
        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.state_machine", QuestTrigger.Enter);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.state_machine");
        Assert.Equal("finish", state.CurrentState);
        Assert.Equal(1, state.Get("start_ticks", 0));
        Assert.Equal(1, state.Get("finish_ticks", 0));
        Assert.True(state.Get("exited_start", false));
        Assert.True(state.Get("entered_finish", false));
    }

    [Fact]
    public async Task ResetAsync_ClearsLoadedStateAndStore()
    {
        var fixture = CreateRuntime("subject-reset");

        await fixture.Runtime.FireQuestAsync(fixture.Context, "test.enter", QuestTrigger.Enter);
        await fixture.Runtime.ResetAsync(fixture.Context.SubjectId);

        var state = await fixture.Runtime.GetStateAsync(fixture.Context.SubjectId, "test.enter");
        Assert.Equal(0, state.Get("enter", 0));
        Assert.Equal(1, fixture.Store.ResetCount);
    }

    private static RuntimeFixture CreateRuntime(string subjectId, bool allowRequirement = true)
    {
        var store = new InMemoryQuestStateStore();
        var sink = new RecordingQuestUpdateSink();
        var requirements = new QuestRequirementRegistry<TestQuestContext>()
            .Add("allow", ctx => allowRequirement);

        var runtime = new QuestRuntime<TestQuestContext>(
            store,
            sink,
            requirements,
            (baseContext, trigger, quest, targetId, value, state, results) => new TestQuestContext
            {
                SubjectId = baseContext.SubjectId,
                Subject = baseContext.Subject,
                Trigger = trigger,
                TargetId = targetId,
                Value = value,
                State = state,
                RequirementResults = results,
                Services = baseContext.Services,
                Events = baseContext.Events,
            });

        runtime.LoadFromAssembly(Assembly.GetExecutingAssembly());

        return new RuntimeFixture(
            runtime,
            store,
            sink,
            new TestQuestContext { SubjectId = subjectId });
    }

    private sealed record RuntimeFixture(
        QuestRuntime<TestQuestContext> Runtime,
        InMemoryQuestStateStore Store,
        RecordingQuestUpdateSink Sink,
        TestQuestContext Context);
}
