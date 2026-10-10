using Altruist.Gaming;
using FluentAssertions;

namespace Tests.Gaming.AI;

/// <summary>RecedingPlanner on a toy problem (the same cases as the TypeScript twin's
/// tests/recedingPlanner.test.ts): a candidate is a number x starting at tick T0, its program presses
/// on its first frame, a rollout scores -(x - target)^2, neighbours are x ± 1.</summary>
public class RecedingPlannerTests
{
    private sealed record Cand(int X, int T0);

    private sealed class Toy : IPlannerOps<Cand, int>
    {
        private readonly int _target;
        public readonly List<int> Evaluated = new();
        public int Draws;
        public int Replans;

        public Toy(int target) => _target = target;

        public int Start(Cand c) => c.T0;
        public int[] Frames(Cand c, int tick) => new[] { 1, 0, 0, 0, 0, 0, 0, 0 };
        public bool Pressed(int f) => f != 0;

        public float Evaluate(Cand c, int[] frames, int offset)
        {
            Evaluated.Add(c.X);
            return -(c.X - _target) * (c.X - _target);
        }

        public float Noise()
        {
            Draws++;
            return 0;
        }

        public IEnumerable<Cand> Neighbours(Cand c) => new[] { c with { X = c.X - 1 }, c with { X = c.X + 1 } };
        public void OnReplan(PlannedPlan<Cand, int> previous) => Replans++;
    }

    private static RecedingPlanner<Cand, int> Planner(float margin = 0.5f) => new(3, margin, 3);

    [Fact]
    public void Runs_the_best_candidate_and_refines_around_it()
    {
        var p = Planner();
        var ops = new Toy(7);
        p.Restart(0, new[] { 0, 4, 8 }.Select(x => new Cand(x, 0)));
        for (var i = 0; i < 4; i++) p.Search(0, 10, ops);
        p.Best!.Value.Cand.X.Should().Be(7);
        p.Plan!.Cand.X.Should().Be(7);
        p.Refined.Should().Be(3);
    }

    [Fact]
    public void Spends_exactly_the_budget_one_unit_per_evaluation_and_per_due_rescoring()
    {
        var p = Planner();
        var ops = new Toy(7);
        // Programs starting at tick 8: none of them goes stale by then.
        p.Restart(0, Enumerable.Range(0, 10).Select(x => new Cand(x, 8)));
        p.Search(5, 4, ops).Should().Be(0);
        ops.Evaluated.Should().HaveCount(4);
        p.Pending.Should().Be(6);
        // Three ticks later the plan is due again: one unit for it, the rest for the queue.
        var running = p.Plan!.Cand.X;
        p.Search(8, 2, ops);
        ops.Evaluated.Skip(4).Should().Equal(running, 4);
    }

    [Fact]
    public void Skips_a_stale_candidate_without_a_unit_or_a_draw()
    {
        var p = Planner();
        var ops = new Toy(7);
        p.Restart(10, new[] { new Cand(7, 4), new Cand(3, 10) });
        p.Search(10, 5, ops).Should().Be(4);
        ops.Evaluated.Should().Equal(3);
        ops.Draws.Should().Be(1);
    }

    [Fact]
    public void Switches_plans_only_past_the_margin_keeping_the_commitment()
    {
        var p = Planner(5);
        var ops = new Toy(0);
        p.Restart(0, new[] { new Cand(3, 0) });
        p.Search(0, 1, ops);
        p.Commit(0).Should().BeTrue();
        p.Commit(1).Should().BeFalse();
        p.Restart(0, new[] { new Cand(2, 0), new Cand(0, 0) });
        // One unit re-scores the running plan (a restart makes it due), two for the queue.
        p.Search(0, 3, ops);
        // x = 2 scores -4 against -9: better by 5, not past the margin; x = 0 (0) is.
        p.Plan!.Cand.X.Should().Be(0);
        p.Plan.Committed.Should().BeTrue();
        p.Plan.CommitTick.Should().Be(0);
        ops.Replans.Should().Be(1);
    }

    [Fact]
    public void Is_settled_when_done_on_a_sure_score_or_after_the_wait()
    {
        var p = Planner();
        var ops = new Toy(7);
        p.Restart(0, new[] { new Cand(7, 0), new Cand(1, 0) });
        p.Search(0, 1, ops);
        p.Settled(1, 10, 3).Should().BeFalse();
        p.Settled(1, -1, 3).Should().BeTrue();
        p.Settled(3, 10, 3).Should().BeTrue();
    }

    [Fact]
    public void Slides_a_plan_a_tick_later_and_rescores_it_next_time()
    {
        var p = Planner();
        var ops = new Toy(7);
        p.Adopt(new Cand(7, 0), new[] { 1, 0, 0 }, 20, 0);
        p.Refined.Should().Be(3);
        p.Pending.Should().Be(0);
        p.Slide();
        p.Plan!.Start.Should().Be(21);
        p.Search(21, 1, ops);
        ops.Evaluated.Should().Equal(7);
        p.Drop()!.Cand.X.Should().Be(7);
        p.Plan.Should().BeNull();
    }
}
