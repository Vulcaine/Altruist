using Altruist.Gaming;

namespace Tests.Gaming.AI;

public class StateMachineInitialStateTests
{
    private sealed class TwoInitials
    {
        [State("A", Initial = true)]
        public string? A(TestAIContext ctx, float dt) => null;

        [State("B", Initial = true)]
        public string? B(TestAIContext ctx, float dt) => null;
    }

    [Fact]
    public void Build_rejects_several_states_without_an_initial_one()
    {
        var b = new StateMachineBuilder<TestAIContext>();
        b.ConfigureState("A").Handler((_, _) => null);
        b.ConfigureState("B").Handler((_, _) => null);

        var ex = Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Contains("No initial state", ex.Message);
    }

    [Fact]
    public void Build_uses_the_only_state_as_initial()
    {
        var b = new StateMachineBuilder<TestAIContext>();
        b.ConfigureState("Only").Handler((_, _) => null);

        Assert.Equal("Only", b.Build().InitialState);
    }

    [Fact]
    public void Build_uses_the_declared_initial_state()
    {
        var b = new StateMachineBuilder<TestAIContext>();
        b.ConfigureState("A").Handler((_, _) => null);
        b.ConfigureState("B").AsInitial().Handler((_, _) => null);

        Assert.Equal("B", b.Build().InitialState);
    }

    [Fact]
    public void RegisterHandlers_rejects_a_class_with_two_initial_states()
    {
        var b = new StateMachineBuilder<TestAIContext>();

        Assert.Throws<InvalidOperationException>(() => b.RegisterHandlers(new TwoInitials()));
    }
}
