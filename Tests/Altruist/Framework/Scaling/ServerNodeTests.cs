/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Altruist;

using Moq;

namespace Tests.Altruist.Framework.Scaling;

public sealed class FakeContributor(string kind, int units = 0, double load = 0) : ICapacityContributor
{
    public string Kind { get; } = kind;
    public volatile int Units = units;
    public double Load = load;
    public int Calls;
    public CapacitySample Sample()
    {
        Interlocked.Increment(ref Calls);
        return new CapacitySample(Units, Load);
    }
}

public sealed class ThrowingContributor : ICapacityContributor
{
    public string Kind => "broken";
    public CapacitySample Sample() => throw new InvalidOperationException("contributor boom");
}

public sealed class FakeParticipant(string kind = "rooms") : IDrainParticipant
{
    public string Kind { get; } = kind;
    public int Begins;
    public int Forces;
    public volatile bool Drained;
    /// <summary>ForceStop drains it (the rest of the work is closed at once).</summary>
    public bool DrainsOnForce = true;
    public Action? OnBegin;
    public bool IsDrained => Drained;
    public void BeginDrain()
    {
        Interlocked.Increment(ref Begins);
        OnBegin?.Invoke();
    }
    public void ForceStop()
    {
        Interlocked.Increment(ref Forces);
        if (DrainsOnForce) Drained = true;
    }
}

public class ServerNodeTests
{
    private static readonly ServerNodeOptions Fast = new()
    {
        DrainTimeout = TimeSpan.FromSeconds(5),
        ForceStopGrace = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private static ServerNode Node(double maxLoad = 0, ServerNodeOptions? options = null, IEnumerable<ICapacityContributor>? contributors = null,
        IEnumerable<IDrainParticipant>? participants = null, Func<ReadyState>? readiness = null) =>
        new((options ?? Fast) with { MaxLoad = maxLoad }, contributors, participants, readiness, "node-1");

    // ------------------------------------------------------------------ capacity

    [Fact]
    public void An_empty_node_is_ready_unlimited_and_accepts_anything()
    {
        using var node = Node();
        var c = node.Capacity();
        Assert.Equal(ServerNodeState.Ready, c.State);
        Assert.True(c.Unlimited);
        Assert.True(c.Accepting);
        Assert.Null(c.Free);
        Assert.Equal(0, c.Utilization);
        Assert.Empty(c.Kinds);
        Assert.Equal("node-1", c.NodeId);
        Assert.True(node.CanAccept(1_000_000));
    }

    [Fact]
    public void Capacity_sums_every_contributor_and_groups_them_by_kind()
    {
        var a = new FakeContributor("rooms", 3, 4.5);
        var b = new FakeContributor("rooms", 2, 2);
        var w = new FakeContributor("worlds", 1, 0);
        using var node = Node(maxLoad: 10, contributors: new ICapacityContributor[] { a, w });
        node.Register(b);
        var c = node.Capacity();
        Assert.Equal(6.5, c.Load, 6);
        Assert.Equal(3.5, c.Free!.Value, 6);
        Assert.Equal(0.65, c.Utilization, 6);
        Assert.Equal(5, c.UnitsOf("rooms"));
        Assert.Equal(1, c.UnitsOf("worlds"));
        Assert.Equal(0, c.UnitsOf("nothing"));
        Assert.Equal(new[] { "rooms", "worlds" }, c.Kinds.Select(k => k.Kind));
        Assert.Equal(6.5, c.Kinds[0].Load, 6);
    }

    [Fact]
    public void A_node_is_full_at_max_load_and_ready_again_when_load_drops()
    {
        var rooms = new FakeContributor("rooms", 9, 9);
        using var node = Node(maxLoad: 10, contributors: new[] { rooms });
        Assert.Equal(ServerNodeState.Ready, node.State);
        Assert.True(node.CanAccept(1));
        Assert.False(node.CanAccept(1.5)); // does not fit
        rooms.Load = 10;
        Assert.Equal(ServerNodeState.Full, node.State);
        Assert.False(node.Capacity().Accepting);
        Assert.False(node.CanAccept(0.1));
        rooms.Load = 12; // over budget (a room admitted before the budget shrank): still full, no negative free
        Assert.Equal(0, node.Capacity().Free!.Value);
        rooms.Load = 7;
        Assert.Equal(ServerNodeState.Ready, node.State);
        Assert.True(node.CanAccept(3));
        Assert.False(node.CanAccept(3.01));
    }

    [Fact]
    public void A_zero_cost_fits_even_a_nearly_full_node_but_not_a_full_one()
    {
        var rooms = new FakeContributor("rooms", 1, 9.999);
        using var node = Node(maxLoad: 10, contributors: new[] { rooms });
        Assert.True(node.CanAccept(0));
        rooms.Load = 10;
        Assert.False(node.CanAccept(0));
    }

    [Fact]
    public void The_node_is_starting_until_the_server_status_is_alive()
    {
        var state = ReadyState.Starting;
        using var node = Node(readiness: () => state);
        Assert.Equal(ServerNodeState.Starting, node.State);
        Assert.False(node.CanAccept());
        state = ReadyState.Alive;
        Assert.Equal(ServerNodeState.Ready, node.State);
        Assert.True(node.CanAccept());
        // A required service dropped (database lost): not ready again.
        state = ReadyState.Failed;
        Assert.Equal(ServerNodeState.Starting, node.State);
        Assert.False(node.CanAccept());
    }

    [Fact]
    public void Bogus_samples_are_clamped_and_a_throwing_contributor_counts_as_empty()
    {
        var odd = new FakeContributor("odd", -3, double.NaN);
        var neg = new FakeContributor("neg", 2, -5);
        var ok = new FakeContributor("rooms", 1, 2);
        using var node = Node(maxLoad: 4, contributors: new ICapacityContributor[] { odd, neg, new ThrowingContributor(), ok });
        var c = node.Capacity();
        Assert.Equal(2, c.Load, 6);
        Assert.Equal(0, c.UnitsOf("odd"));
        Assert.Equal(2, c.UnitsOf("neg"));
        Assert.Equal(0, c.UnitsOf("broken"));
        Assert.Equal(ServerNodeState.Ready, c.State);
        Assert.True(node.CanAccept(2));
    }

    [Fact]
    public void Runtime_registrations_come_and_go_and_disposing_twice_is_harmless()
    {
        using var node = Node();
        var a = new FakeContributor("rooms", 2, 2);
        var reg = node.Register(a);
        Assert.Equal(2, node.Capacity().UnitsOf("rooms"));
        reg.Dispose();
        reg.Dispose();
        Assert.Equal(0, node.Capacity().UnitsOf("rooms"));
    }

    [Fact]
    public void A_contributor_registered_twice_is_counted_once()
    {
        var a = new FakeContributor("rooms", 2, 2);
        using var node = Node(contributors: new[] { a });
        node.Register(a);
        Assert.Equal(2, node.Capacity().UnitsOf("rooms"));
    }

    [Fact]
    public void Invalid_settings_and_costs_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Node(maxLoad: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerNode(Fast with { DrainTimeout = TimeSpan.FromSeconds(-1) }));
        using var node = Node();
        Assert.Throws<ArgumentOutOfRangeException>(() => node.CanAccept(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => node.CanAccept(double.NaN));
        Assert.Throws<ArgumentNullException>(() => node.Register((ICapacityContributor)null!));
        Assert.Throws<ArgumentNullException>(() => node.Register((IDrainParticipant)null!));
    }

    [Fact]
    public void The_DI_constructor_reads_config_values_status_and_process_id()
    {
        var status = new Mock<IServerStatus>();
        status.Setup(s => s.Status).Returns(ReadyState.Starting);
        var context = new Mock<IAltruistContext>();
        context.Setup(c => c.ProcessId).Returns("proc-7");
        var rooms = new FakeContributor("rooms", 1, 1);
        using var node = new ServerNode(
            new Lazy<IEnumerable<ICapacityContributor>>(() => new[] { rooms }),
            new Lazy<IEnumerable<IDrainParticipant>>(Array.Empty<IDrainParticipant>),
            maxLoad: 5, drainTimeoutSeconds: 12, forceGraceSeconds: 2, status.Object, context.Object);
        Assert.Equal("proc-7", node.NodeId);
        Assert.Equal(5, node.MaxLoad);
        Assert.Equal(TimeSpan.FromSeconds(12), node.DrainTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), node.Options.ForceStopGrace);
        Assert.Equal(ServerNodeState.Starting, node.State);
        status.Setup(s => s.Status).Returns(ReadyState.Alive);
        Assert.Equal(ServerNodeState.Ready, node.State);
        Assert.Equal(1, node.Capacity().UnitsOf("rooms"));
    }

    [Fact]
    public void Contributors_are_resolved_lazily_on_the_first_report()
    {
        var resolved = false;
        using var node = new ServerNode(
            new Lazy<IEnumerable<ICapacityContributor>>(() => { resolved = true; return Array.Empty<ICapacityContributor>(); }),
            new Lazy<IEnumerable<IDrainParticipant>>(Array.Empty<IDrainParticipant>),
            0, 30, 5);
        Assert.False(resolved);
        node.Capacity();
        Assert.True(resolved);
    }

    [Fact]
    public async Task Capacity_reads_are_safe_while_contributors_come_and_go()
    {
        using var node = Node(maxLoad: 1000);
        var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var errors = new ConcurrentQueue<Exception>();
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var reg = node.Register(new FakeContributor("rooms", 1, 1));
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var c = node.Capacity();
                    Assert.InRange(c.UnitsOf("rooms"), 0, 4);
                    node.CanAccept(1);
                }
                catch (Exception ex) { errors.Enqueue(ex); }
            }
        }));
        await Task.WhenAll(writers.Concat(readers));
        Assert.Empty(errors);
        Assert.Equal(0, node.Capacity().UnitsOf("rooms"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void The_report_serializes_to_json_limited_or_not(double maxLoad)
    {
        // Regression: an unlimited node reported Free = infinity, which System.Text.Json refuses.
        using var node = Node(maxLoad: maxLoad, contributors: new[] { new FakeContributor("rooms", 2, 2) });
        var json = System.Text.Json.JsonSerializer.Serialize(node.Capacity(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var free = doc.RootElement.GetProperty("free");
        if (maxLoad == 0) Assert.Equal(System.Text.Json.JsonValueKind.Null, free.ValueKind);
        else Assert.Equal(8, free.GetDouble());
        Assert.Equal(2, doc.RootElement.GetProperty("load").GetDouble());
    }

    // ------------------------------------------------------------------ drain

    [Fact]
    public async Task A_node_without_participants_drains_at_once()
    {
        using var node = Node();
        Assert.True(await node.DrainAsync());
        Assert.Equal(ServerNodeState.Drained, node.State);
        Assert.False(node.WasForced);
    }

    [Fact]
    public async Task A_drain_stops_new_work_tells_every_participant_once_and_waits_for_them()
    {
        var rooms = new FakeParticipant("rooms");
        var worlds = new FakeParticipant("worlds");
        var load = new FakeContributor("rooms", 1, 1);
        using var node = Node(maxLoad: 10, contributors: new[] { load }, participants: new[] { rooms });
        node.Register(worlds);
        var started = 0;
        node.DrainStarted += () => started++;

        var drain = node.DrainAsync();
        Assert.True(node.IsDraining);
        Assert.Equal(ServerNodeState.Draining, node.State);
        Assert.False(node.CanAccept(0));
        Assert.Equal(1, rooms.Begins);
        Assert.Equal(1, worlds.Begins);
        Assert.Equal(1, started);

        await Task.Delay(50);
        Assert.False(drain.IsCompleted);
        rooms.Drained = true;
        await Task.Delay(50);
        Assert.False(drain.IsCompleted);
        worlds.Drained = true;
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ServerNodeState.Drained, node.State);
        Assert.Equal(0, rooms.Forces + worlds.Forces);
        Assert.False(node.Capacity().Accepting);
    }

    [Fact]
    public async Task Every_caller_shares_one_drain()
    {
        var p = new FakeParticipant();
        using var node = Node(participants: new[] { p });
        var first = node.DrainAsync(TimeSpan.FromSeconds(30));
        var second = node.DrainAsync(TimeSpan.FromMilliseconds(1)); // the running drain's budget wins
        var started = 0;
        node.DrainStarted += () => started++;
        node.DrainAsync();
        Assert.Equal(1, p.Begins);
        Assert.Equal(0, started);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        Assert.Equal(0, p.Forces);
        p.Drained = true;
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_participant_that_joins_a_running_drain_is_told_at_once()
    {
        var first = new FakeParticipant();
        using var node = Node(participants: new[] { first });
        var drain = node.DrainAsync();
        var late = new FakeParticipant("late");
        node.Register(late);
        Assert.Equal(1, late.Begins);
        first.Drained = true;
        await Task.Delay(50);
        Assert.False(drain.IsCompleted); // the late one still runs
        late.Drained = true;
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_timed_out_drain_stops_only_what_still_runs()
    {
        var done = new FakeParticipant("done") { Drained = true };
        var slow = new FakeParticipant("slow");
        using var node = Node(participants: new IDrainParticipant[] { done, slow });
        var sw = Stopwatch.StartNew();
        Assert.False(await node.DrainAsync(TimeSpan.FromMilliseconds(100)));
        Assert.InRange(sw.ElapsedMilliseconds, 90, 4000);
        Assert.Equal(0, done.Forces);
        Assert.Equal(1, slow.Forces);
        Assert.True(node.WasForced);
        Assert.Equal(ServerNodeState.Drained, node.State);

        // Joining after the drain was forced: told to begin and to stop right away.
        var after = new FakeParticipant("after");
        node.Register(after);
        Assert.Equal(1, after.Begins);
        Assert.Equal(1, after.Forces);
    }

    [Fact]
    public async Task Work_that_ignores_the_force_stop_holds_the_drain_only_for_the_grace()
    {
        var stuck = new FakeParticipant("stuck") { DrainsOnForce = false };
        using var node = Node(options: Fast with { ForceStopGrace = TimeSpan.FromMilliseconds(150) }, participants: new[] { stuck });
        var sw = Stopwatch.StartNew();
        Assert.False(await node.DrainAsync(TimeSpan.FromMilliseconds(50)));
        Assert.InRange(sw.ElapsedMilliseconds, 190, 4000);
        Assert.Equal(1, stuck.Forces);
        Assert.Equal(ServerNodeState.Drained, node.State);
    }

    [Fact]
    public async Task A_zero_timeout_forces_at_once()
    {
        var p = new FakeParticipant();
        using var node = Node(participants: new[] { p });
        Assert.False(await node.DrainAsync(TimeSpan.Zero));
        Assert.Equal(1, p.Forces);
    }

    [Fact]
    public async Task Throwing_participants_cannot_hang_or_break_the_drain()
    {
        var boomBegin = new Mock<IDrainParticipant>();
        boomBegin.Setup(p => p.Kind).Returns("boom");
        boomBegin.Setup(p => p.BeginDrain()).Throws(new InvalidOperationException("begin"));
        boomBegin.Setup(p => p.IsDrained).Returns(true);
        var boomState = new Mock<IDrainParticipant>();
        boomState.Setup(p => p.Kind).Returns("state");
        boomState.Setup(p => p.IsDrained).Throws(new InvalidOperationException("state"));
        var fine = new FakeParticipant();
        using var node = Node(participants: new[] { boomBegin.Object, boomState.Object, fine });
        node.DrainStarted += () => throw new InvalidOperationException("handler");
        var drain = node.DrainAsync();
        Assert.Equal(1, fine.Begins);
        fine.Drained = true;
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelling_a_caller_only_stops_its_wait()
    {
        var p = new FakeParticipant();
        using var node = Node(participants: new[] { p });
        using var cts = new CancellationTokenSource();
        var waiting = node.DrainAsync(cancellationToken: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(node.IsDraining);
        p.Drained = true;
        Assert.True(await node.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_participant_removed_during_the_drain_no_longer_holds_it()
    {
        var p = new FakeParticipant();
        using var node = Node();
        var reg = node.Register(p);
        var drain = node.DrainAsync();
        await Task.Delay(30);
        Assert.False(drain.IsCompleted);
        reg.Dispose();
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Drain_on_shutdown_drains_the_node_unless_disabled()
    {
        var p = new FakeParticipant { Drained = true };
        using var node = Node(participants: new[] { p });
        await new ServerDrainOnShutdown(node, enabled: false).StoppingAsync(CancellationToken.None);
        Assert.False(node.IsDraining);
        await new ServerDrainOnShutdown(node).StoppingAsync(CancellationToken.None);
        Assert.Equal(ServerNodeState.Drained, node.State);
        Assert.Equal(1, p.Begins);
    }

    [Fact]
    public async Task Drain_on_shutdown_gives_up_quietly_when_the_host_stops_waiting()
    {
        var p = new FakeParticipant();
        using var node = Node(participants: new[] { p });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await new ServerDrainOnShutdown(node).StoppingAsync(cts.Token); // does not throw
        Assert.True(node.IsDraining);
    }

    // ------------------------------------------------------------------ metrics

    [Fact]
    public void The_meter_reports_load_budget_state_and_units_per_kind()
    {
        var rooms = new FakeContributor("rooms", 3, 6);
        var worlds = new FakeContributor("worlds", 2, 0);
        using var node = Node(maxLoad: 10, contributors: new ICapacityContributor[] { rooms, worlds });
        var seen = new ConcurrentDictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, node.Metrics)) l.EnableMeasurementEvents(instrument);
        };
        void Record(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var kind = tags.Length > 0 ? ":" + tags[0].Value : "";
            seen[i.Name + kind] = v;
        }
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        listener.Start();

        listener.RecordObservableInstruments();
        Assert.Equal(6, seen["altruist.server.load"]);
        Assert.Equal(10, seen["altruist.server.max_load"]);
        Assert.Equal((int)ServerNodeState.Ready, seen["altruist.server.state"]);
        Assert.Equal(1, seen["altruist.server.accepting"]);
        Assert.Equal(3, seen["altruist.server.units:rooms"]);
        Assert.Equal(2, seen["altruist.server.units:worlds"]);
        Assert.Equal(6, seen["altruist.server.kind_load:rooms"]);

        rooms.Load = 10;
        listener.RecordObservableInstruments();
        Assert.Equal((int)ServerNodeState.Full, seen["altruist.server.state"]);
        Assert.Equal(0, seen["altruist.server.accepting"]);
    }
}
