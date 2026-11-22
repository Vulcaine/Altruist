using Altruist.Gaming;

namespace Tests.Gaming.AI;

/// <summary>
/// AI agents outside the world organizer: a context with no world entity, a state machine
/// created from the behavior type (no assembly scan) and ticked by its owner.
/// </summary>
public class AIAgentTests
{
    public sealed class PatrolContext : AIContext
    {
        public int Ticks;
        public int Turns;
        public bool WallAhead;
        public List<string> Log { get; } = new();
    }

    [AIBehavior("test_patrol_agent")]
    public sealed class PatrolBehavior
    {
        [AIState("Walk", Initial = true)]
        public string? Walk(PatrolContext ctx, float dt)
        {
            ctx.Ticks++;
            return ctx.WallAhead ? "Turn" : null;
        }

        [AIState("Turn")]
        public string? Turn(PatrolContext ctx, float dt)
        {
            ctx.Turns++;
            ctx.WallAhead = false;
            return "Walk";
        }

        [AIStateEnter("Turn")]
        public void EnterTurn(PatrolContext ctx) => ctx.Log.Add("enter:Turn");

        [AIStateExit("Walk")]
        public void ExitWalk(PatrolContext ctx) => ctx.Log.Add("exit:Walk");
    }

    [Fact]
    public void A_context_without_a_world_entity_drives_a_behavior()
    {
        var ctx = new PatrolContext();
        var fsm = AIBehaviorDiscovery.CreateStateMachine<PatrolBehavior>();
        fsm.Initialize(ctx);
        Assert.Equal("Walk", fsm.CurrentStateName);

        fsm.Update(ctx, 1f / 60f);
        ctx.WallAhead = true;
        Assert.True(fsm.Update(ctx, 1f / 60f));
        Assert.Equal("Turn", fsm.CurrentStateName);
        Assert.True(fsm.Update(ctx, 1f / 60f));
        Assert.Equal("Walk", fsm.CurrentStateName);

        Assert.Equal(2, ctx.Ticks);
        Assert.Equal(1, ctx.Turns);
        Assert.Equal(new[] { "exit:Walk", "enter:Turn" }, ctx.Log);
    }

    [Fact]
    public void Each_agent_has_its_own_machine_over_one_shared_template()
    {
        var a = new PatrolContext();
        var b = new PatrolContext();
        var fa = AIBehaviorDiscovery.CreateStateMachine<PatrolBehavior>();
        var fb = AIBehaviorDiscovery.CreateStateMachine<PatrolBehavior>();
        fa.Initialize(a);
        fb.Initialize(b);
        fa.TransitionTo(a, "Turn");
        Assert.Equal("Turn", fa.CurrentStateName);
        Assert.Equal("Walk", fb.CurrentStateName);
        Assert.True(AIBehaviorDiscovery.HasBehavior("test_patrol_agent"));
        Assert.NotNull(AIBehaviorDiscovery.CreateStateMachine("test_patrol_agent"));
    }

    [Fact]
    public void A_forced_transition_runs_exit_and_enter_hooks()
    {
        var ctx = new PatrolContext();
        var fsm = AIBehaviorDiscovery.CreateStateMachine<PatrolBehavior>();
        fsm.Initialize(ctx);
        fsm.TransitionTo(ctx, "Turn");
        Assert.Equal(new[] { "exit:Walk", "enter:Turn" }, ctx.Log);
        Assert.Equal(0f, ctx.TimeInState);
    }

    [Fact]
    public void A_type_without_the_attribute_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => AIBehaviorDiscovery.CreateStateMachine<object>());
    }

    // Two behaviors that share a name: each type must still get its own handlers.
    [AIBehavior("test_name_collision")]
    public sealed class FirstNamesake
    {
        [AIState("First", Initial = true)]
        public string? First(PatrolContext ctx, float dt) => null;
    }

    [AIBehavior("test_name_collision")]
    public sealed class SecondNamesake
    {
        [AIState("Second", Initial = true)]
        public string? Second(PatrolContext ctx, float dt) => null;
    }

    [Fact]
    public void A_behavior_type_builds_its_own_machine_when_another_type_holds_the_name()
    {
        var first = AIBehaviorDiscovery.CreateStateMachine<FirstNamesake>();
        var second = AIBehaviorDiscovery.CreateStateMachine<SecondNamesake>();
        var a = new PatrolContext();
        var b = new PatrolContext();
        first.Initialize(a);
        second.Initialize(b);
        Assert.Equal("First", first.CurrentStateName);
        Assert.Equal("Second", second.CurrentStateName);

        // Cached per type: asking again keeps each type's own template.
        var again = AIBehaviorDiscovery.CreateStateMachine<SecondNamesake>();
        again.Initialize(new PatrolContext());
        Assert.Equal("Second", again.CurrentStateName);
        Assert.True(AIBehaviorDiscovery.HasBehavior("test_name_collision"));
    }
}
