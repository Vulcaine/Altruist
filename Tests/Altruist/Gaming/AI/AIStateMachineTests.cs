using System.Reflection;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.AI;

#region Test Helpers

public class TestWorldObject : ITypelessWorldObject
{
    public bool Expired { get; set; }
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();
    public string? ObjectArchetype { get; set; }
    public string ZoneId { get; set; } = "test_zone";
    public uint VirtualId { get; set; } = 1;
}

public class TestAIContext : IAIContext
{
    public ITypelessWorldObject Entity { get; set; } = new TestWorldObject();
    public float TimeInState { get; set; }
    public bool IdleEntered { get; set; }
    public bool IdleExited { get; set; }
    public bool ChaseEntered { get; set; }
    public bool ChaseExited { get; set; }
    public bool AttackEntered { get; set; }
    public string ForceNextState { get; set; } = "";
}

[AIBehavior("test_basic")]
public class TestBasicBehavior
{
    [AIState("Idle", Initial = true)]
    public string? Idle(TestAIContext ctx, float dt)
    {
        if (!string.IsNullOrEmpty(ctx.ForceNextState))
        {
            var next = ctx.ForceNextState;
            ctx.ForceNextState = "";
            return next;
        }
        return null;
    }

    [AIStateEnter("Idle")]
    public void IdleEnter(TestAIContext ctx) => ctx.IdleEntered = true;

    [AIStateExit("Idle")]
    public void IdleExit(TestAIContext ctx) => ctx.IdleExited = true;

    [AIState("Chase")]
    public string? Chase(TestAIContext ctx, float dt)
    {
        if (!string.IsNullOrEmpty(ctx.ForceNextState))
        {
            var next = ctx.ForceNextState;
            ctx.ForceNextState = "";
            return next;
        }
        return null;
    }

    [AIStateEnter("Chase")]
    public void ChaseEnter(TestAIContext ctx) => ctx.ChaseEntered = true;

    [AIStateExit("Chase")]
    public void ChaseExit(TestAIContext ctx) => ctx.ChaseExited = true;

    [AIState("Attack")]
    public string? Attack(TestAIContext ctx, float dt)
    {
        if (!string.IsNullOrEmpty(ctx.ForceNextState))
        {
            var next = ctx.ForceNextState;
            ctx.ForceNextState = "";
            return next;
        }
        return null;
    }

    [AIStateEnter("Attack")]
    public void AttackEnter(TestAIContext ctx) => ctx.AttackEntered = true;
}

[AIBehavior("test_delayed")]
public class TestDelayedBehavior
{
    public static int UpdateCallCount;

    [AIState("Wait", Initial = true, Delay = 2f)]
    public string? Wait(TestAIContext ctx, float dt)
    {
        Interlocked.Increment(ref UpdateCallCount);
        return null;
    }
}

[AIBehavior("test_no_initial")]
public class TestNoInitialBehavior
{
    [AIState("Alpha")]
    public string? Alpha(TestAIContext ctx, float dt) => null;

    [AIState("Beta")]
    public string? Beta(TestAIContext ctx, float dt) => null;
}

#endregion

#region Fixture

public class AIDiscoveryFixture : IDisposable
{
    public AIDiscoveryFixture()
    {
        ResetDiscovery();
        AIBehaviorDiscovery.DiscoverBehaviors(
            [typeof(TestBasicBehavior).Assembly],
            t => Activator.CreateInstance(t)!,
            NullLoggerFactory.Instance.CreateLogger("test"));
    }

    public void Dispose() => ResetDiscovery();

    private static void ResetDiscovery()
    {
        var discoveredField = typeof(AIBehaviorDiscovery)
            .GetField("_discovered", BindingFlags.Static | BindingFlags.NonPublic);
        discoveredField?.SetValue(null, false);

        var templatesField = typeof(AIBehaviorDiscovery)
            .GetField("_templates", BindingFlags.Static | BindingFlags.NonPublic);
        if (templatesField?.GetValue(null) is System.Collections.IDictionary dict)
            dict.Clear();
    }
}

[CollectionDefinition("AI")]
public class AICollection : ICollectionFixture<AIDiscoveryFixture> { }

#endregion

#region AIStateMachine Tests

[Collection("AI")]
public class AIStateMachineTests
{
    private AIStateMachine CreateFSM(string name = "test_basic")
        => AIBehaviorDiscovery.CreateStateMachine(name)!;

    [Fact]
    public void Constructor_ShouldSetCurrentStateToInitial()
    {
        var fsm = CreateFSM();

        Assert.Equal("Idle", fsm.CurrentStateName);
    }

    [Fact]
    public void Reset_ShouldFireEnterHookForInitialState()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // Transition away, then reset to verify enter hook fires
        fsm.TransitionTo(ctx, "Chase");
        ctx.IdleEntered = false;

        fsm.Reset(ctx);

        Assert.True(ctx.IdleEntered);
    }

    [Fact]
    public void Update_ShouldStayInState_WhenReturnsNull()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        var transitioned = fsm.Update(ctx, 0.04f);

        Assert.False(transitioned);
        Assert.Equal("Idle", fsm.CurrentStateName);
    }

    [Fact]
    public void Update_ShouldTransition_WhenReturnsNewStateName()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        ctx.ForceNextState = "Chase";
        var transitioned = fsm.Update(ctx, 0.04f);

        Assert.True(transitioned);
        Assert.Equal("Chase", fsm.CurrentStateName);
    }

    [Fact]
    public void Update_ShouldNotTransition_WhenReturnsSameStateName()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        ctx.IdleExited = false;
        ctx.IdleEntered = false;
        ctx.ForceNextState = "Idle"; // same state
        var transitioned = fsm.Update(ctx, 0.04f);

        Assert.False(transitioned); // no transition because same state
    }

    [Fact]
    public void Update_ShouldNotTransition_WhenReturnsUnknownStateName()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        ctx.ForceNextState = "NonExistent";
        var transitioned = fsm.Update(ctx, 0.04f);

        Assert.False(transitioned);
        Assert.Equal("Idle", fsm.CurrentStateName);
    }

    [Fact]
    public void Transition_ShouldFireExitThenEnter()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it
        ctx.IdleExited = false;
        ctx.ChaseEntered = false;

        ctx.ForceNextState = "Chase";
        fsm.Update(ctx, 0.04f);

        Assert.True(ctx.IdleExited);
        Assert.True(ctx.ChaseEntered);
    }

    [Fact]
    public void Transition_ShouldResetTimeInState()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        // Accumulate time
        fsm.Update(ctx, 1f);
        fsm.Update(ctx, 1f);
        Assert.True(fsm.TimeInState >= 2f);

        // Transition resets
        ctx.ForceNextState = "Chase";
        fsm.Update(ctx, 0.04f);

        Assert.True(fsm.TimeInState < 0.1f);
    }

    [Fact]
    public void TimeInState_ShouldAccumulateAcrossTicks()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        fsm.Update(ctx, 0.5f);
        fsm.Update(ctx, 0.5f);
        fsm.Update(ctx, 0.5f);

        Assert.True(fsm.TimeInState >= 1.4f);
        Assert.Equal(fsm.TimeInState, ctx.TimeInState);
    }

    [Fact]
    public void Delay_ShouldSkipUpdate_UntilDelayElapsed()
    {
        var fsm = AIBehaviorDiscovery.CreateStateMachine("test_delayed")!;
        var ctx = new TestAIContext();
        TestDelayedBehavior.UpdateCallCount = 0;
        // FSM starts in initial state from constructor; use Update to drive it

        // Tick 1s — delay is 2s, should NOT call update
        fsm.Update(ctx, 1f);
        Assert.Equal(0, TestDelayedBehavior.UpdateCallCount);
    }

    [Fact]
    public void Delay_ShouldRunUpdate_AfterDelayElapsed()
    {
        var fsm = AIBehaviorDiscovery.CreateStateMachine("test_delayed")!;
        var ctx = new TestAIContext();
        TestDelayedBehavior.UpdateCallCount = 0;
        // FSM starts in initial state from constructor; use Update to drive it

        // Tick past the 2s delay
        fsm.Update(ctx, 1f);
        fsm.Update(ctx, 1.5f); // total 2.5s > 2s delay
        Assert.True(TestDelayedBehavior.UpdateCallCount > 0);
    }

    [Fact]
    public void TransitionTo_ShouldForceTransition()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        fsm.TransitionTo(ctx, "Attack");

        Assert.Equal("Attack", fsm.CurrentStateName);
        Assert.True(ctx.AttackEntered);
    }

    [Fact]
    public void TransitionTo_ShouldIgnoreUnknownState()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it

        fsm.TransitionTo(ctx, "DoesNotExist");

        Assert.Equal("Idle", fsm.CurrentStateName);
    }

    [Fact]
    public void Reset_ShouldReturnToInitialState()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it
        fsm.TransitionTo(ctx, "Chase");
        Assert.Equal("Chase", fsm.CurrentStateName);

        fsm.Reset(ctx);

        Assert.Equal("Idle", fsm.CurrentStateName);
    }

    [Fact]
    public void Reset_ShouldFireExitAndEnter()
    {
        var fsm = CreateFSM();
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it
        fsm.TransitionTo(ctx, "Chase");

        ctx.ChaseExited = false;
        ctx.IdleEntered = false;

        fsm.Reset(ctx);

        Assert.True(ctx.ChaseExited);
        Assert.True(ctx.IdleEntered);
    }
}

#endregion

#region AIBehaviorDiscovery Tests

[Collection("AI")]
public class AIBehaviorDiscoveryTests
{
    [Fact]
    public void DiscoverBehaviors_ShouldFindAnnotatedClasses()
    {
        Assert.True(AIBehaviorDiscovery.HasBehavior("test_basic"));
        Assert.True(AIBehaviorDiscovery.HasBehavior("test_delayed"));
    }

    [Fact]
    public void CreateStateMachine_ShouldReturnFSM_ForKnownBehavior()
    {
        var fsm = AIBehaviorDiscovery.CreateStateMachine("test_basic");
        Assert.NotNull(fsm);
    }

    [Fact]
    public void CreateStateMachine_ShouldReturnNull_ForUnknownBehavior()
    {
        var fsm = AIBehaviorDiscovery.CreateStateMachine("nonexistent");
        Assert.Null(fsm);
    }

    [Fact]
    public void CreateStateMachine_ShouldReturnSeparateInstances()
    {
        var fsm1 = AIBehaviorDiscovery.CreateStateMachine("test_basic");
        var fsm2 = AIBehaviorDiscovery.CreateStateMachine("test_basic");

        Assert.NotSame(fsm1, fsm2);
    }

    [Fact]
    public void NoInitialMarked_ShouldUseFirstState()
    {
        var fsm = AIBehaviorDiscovery.CreateStateMachine("test_no_initial");
        Assert.NotNull(fsm);
        // Should use first discovered state (Alpha or Beta)
        var ctx = new TestAIContext();
        // FSM starts in initial state from constructor; use Update to drive it
        Assert.True(fsm.CurrentStateName == "Alpha" || fsm.CurrentStateName == "Beta");
    }
}

#endregion
