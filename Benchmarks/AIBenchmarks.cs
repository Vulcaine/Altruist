using Altruist.Gaming;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks the AI state machine system.
/// Measures: FSM tick (compiled delegates), state transitions, discovery.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class AIBenchmarks
{
    private AIStateMachine _fsm = null!;
    private BenchAIContext _ctx = null!;

    public class BenchWorldObj : ITypelessWorldObject
    {
        public bool Expired { get; set; }
        public string InstanceId { get; set; } = "bench";
        public string? ObjectArchetype { get; set; }
        public string ZoneId { get; set; } = "";
        public uint VirtualId { get; set; }
    }

    public class BenchAIContext : IAIContext
    {
        public ITypelessWorldObject Entity { get; set; } = new BenchWorldObj();
        public float TimeInState { get; set; }
        public bool ShouldChase { get; set; }
        public bool ShouldAttack { get; set; }
    }

    [AIBehavior("bench_ai")]
    public class BenchBehavior
    {
        [AIState("Idle", Initial = true)]
        public string? Idle(BenchAIContext ctx, float dt)
            => ctx.ShouldChase ? "Chase" : null;

        [AIState("Chase")]
        public string? Chase(BenchAIContext ctx, float dt)
            => ctx.ShouldAttack ? "Attack" : null;

        [AIState("Attack")]
        public string? Attack(BenchAIContext ctx, float dt)
            => ctx.TimeInState > 1f ? "Idle" : null;

        [AIStateEnter("Chase")]
        public void ChaseEnter(BenchAIContext ctx) { }

        [AIStateExit("Chase")]
        public void ChaseExit(BenchAIContext ctx) { }
    }

    [GlobalSetup]
    public void Setup()
    {
        // Reset and discover
        var discoveredField = typeof(AIBehaviorDiscovery)
            .GetField("_discovered", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        discoveredField?.SetValue(null, false);
        var templatesField = typeof(AIBehaviorDiscovery)
            .GetField("_templates", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (templatesField?.GetValue(null) is System.Collections.IDictionary dict) dict.Clear();

        AIBehaviorDiscovery.DiscoverBehaviors(
            [typeof(BenchBehavior).Assembly],
            t => Activator.CreateInstance(t)!,
            NullLoggerFactory.Instance.CreateLogger("bench"));

        _fsm = AIBehaviorDiscovery.CreateStateMachine("bench_ai")!;
        _ctx = new BenchAIContext();
    }

    [Benchmark(Description = "FSM.Update - stay in state (no transition)")]
    public void UpdateNoTransition()
    {
        _ctx.ShouldChase = false;
        _fsm.Update(_ctx, 0.04f);
    }

    [Benchmark(Description = "FSM.Update - transition (exit+enter hooks)")]
    public void UpdateWithTransition()
    {
        _ctx.ShouldChase = true;
        _fsm.Update(_ctx, 0.04f);
        // Reset back
        _ctx.ShouldChase = false;
        _ctx.ShouldAttack = false;
        _fsm.TransitionTo(_ctx, "Idle");
    }

    [Benchmark(Description = "CreateStateMachine (from cached template)")]
    public AIStateMachine CreateFSM()
    {
        return AIBehaviorDiscovery.CreateStateMachine("bench_ai")!;
    }

    [Benchmark(Description = "FSM.Update x1000 entities (simulate tick)")]
    [Arguments(1000)]
    [Arguments(5000)]
    public void TickManyEntities(int count)
    {
        for (int i = 0; i < count; i++)
            _fsm.Update(_ctx, 0.04f);
    }
}
