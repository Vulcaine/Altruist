/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Lobbies;
using Altruist.Gaming.Matchmaking;
using Altruist.Gaming.Rooms;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Tests.Gaming.Rooms;

// ------------------------------------------------------------------ a physics game for the parallel steps

/// <summary>Every seat is a puck in a Box2D box, pushed by its input; the pucks collide.</summary>
public sealed class PuckSim : IRoomSimulation<Walk>
{
    private readonly IPhysxWorldEngine2D _world;
    private readonly Dictionary<int, IPhysxBody2D> _pucks = new();
    public readonly ConcurrentDictionary<int, bool> Threads = new();
    public int Steps;
    public int EndAfter = int.MaxValue;
    public int ThrowAt = -1;
    public int BeforeSteps;

    public PuckSim()
    {
        _world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = Vector2.Zero, VelocityIterations = 8, PositionIterations = 3 });
        var walls = _world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
        _world.CreateFixture(walls, new PhysxFixtureDef2D
        {
            Shape = PhysxShape2D.Chain(new[] { new Vector2(-6, -4), new Vector2(6, -4), new Vector2(6, 4), new Vector2(-6, 4) }, loop: true),
        });
    }

    public float Dt => 1f / 60f;
    public bool Ended => Steps >= EndAfter;

    public void AddSeat(int seat, int team)
    {
        var body = _world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(team == 0 ? -3 : 3, seat % 5 - 2), LinearDamping = 0.5f });
        _world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1, Restitution = 0.6f });
        _pucks[seat] = body;
    }

    public void RemoveSeat(int seat)
    {
        if (_pucks.Remove(seat, out var body)) _world.RemoveBody(body);
    }

    public void SetBotControlled(int seat, bool bot) { }

    public Vector2 PositionOf(int seat) => _pucks[seat].Position;

    public void Step(IReadOnlyDictionary<int, Walk> inputs)
    {
        Threads[Environment.CurrentManagedThreadId] = true;
        if (Steps == ThrowAt) throw new InvalidOperationException("sim boom");
        foreach (var (seat, w) in inputs)
            if (_pucks.TryGetValue(seat, out var b))
                b.LinearVelocity += new Vector2(w.Dx * 0.05f, (seat % 2 == 0 ? 1 : -1) * 0.03f * w.Dx);
        _world.Step(Dt);
        Steps++;
    }

    public ulong Hash()
    {
        var h = 1469598103934665603UL;
        foreach (var (seat, b) in _pucks.OrderBy(p => p.Key))
        {
            h = (h ^ (ulong)seat) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(b.Position.X)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(b.Position.Y)) * 1099511628211UL;
            h = (h ^ (uint)BitConverter.SingleToInt32Bits(b.LinearVelocity.X)) * 1099511628211UL;
        }
        return h ^ (ulong)Steps;
    }
}

public sealed class PuckGame : IRoomGame<PuckSim, Walk, Player>, IRoomTransport
{
    public readonly List<(string Client, string Packet)> Out = new();
    public readonly List<string> Finished = new();
    public readonly List<string> ForcedClosed = new();
    public readonly ConcurrentDictionary<int, bool> SendThreads = new();
    public Func<string, int> EndAfter = _ => int.MaxValue;

    public List<string> To(string client) => Out.Where(o => o.Client == client).Select(o => o.Packet).ToList();

    public void Send(string clientId, IPacketBase packet)
    {
        SendThreads[Environment.CurrentManagedThreadId] = true;
        Out.Add((clientId, ((Sent)packet).ToString()));
    }

    public void Disconnect(string clientId) { }

    public Walk NeutralInput => default;
    public string NameOf(Player player) => player.Name;
    public string BotNameOf(Room<PuckSim, Walk, Player> room, Seat<PuckSim, Walk, Player> seat) => $"bot{seat.Id}";
    public PuckSim CreateSimulation(string mode, string? playlist) => new() { EndAfter = EndAfter(mode) };
    public double LoadOf(string mode, string? playlist) => mode == "big" ? 2 : 1;

    /// <summary>A bot that steers by its own room's state (reads the simulation it drives).</summary>
    public ISeatController<Walk> CreateBot(Room<PuckSim, Walk, Player> room, Seat<PuckSim, Walk, Player> seat) => new Chaser(room.Sim, seat.Id);

    public void BeforeStep(Room<PuckSim, Walk, Player> room) => room.Sim.BeforeSteps++;
    public void OnFinished(Room<PuckSim, Walk, Player> room) => Finished.Add(room.Id);
    public void OnForcedClose(Room<PuckSim, Walk, Player> room) => ForcedClosed.Add(room.Id);

    public IPacketBase? Welcome(Room<PuckSim, Walk, Player> room, Participant<PuckSim, Walk, Player> p) => new Sent("welcome", $"{p.Seat!.Id}");
    public IPacketBase? Roster(Room<PuckSim, Walk, Player> room) => new Sent("roster", room.Seats.Count.ToString());
    public IPacketBase? Notice(Room<PuckSim, Walk, Player> room, RoomNoticeKind kind, int seat, string name) => new Sent("notice", $"{kind}:{seat}");
    public object CaptureSnapshot(Room<PuckSim, Walk, Player> room) => $"{room.Sim.Steps}:{room.Sim.Hash():x}";
    public IPacketBase PersonalizeSnapshot(object shared, Participant<PuckSim, Walk, Player> p) => new Sent("snap", $"{shared}:ack{p.Input.LastAppliedSeq}");
    public IPacketBase? ReturnedToMenu(Room<PuckSim, Walk, Player> room) => new Sent("menu", room.Mode);
    public IPacketBase? NothingToRejoin() => new Sent("concluded", "");
    public IPacketBase? ServerDraining() => new Sent("draining", "");

    private sealed class Chaser(PuckSim sim, int seat) : ISeatController<Walk>
    {
        public Walk Think() => new(sim.PositionOf(seat).X < 0 ? 2 : -2);
    }
}

/// <summary>
/// Hosting rooms on a fleet of servers: rooms stepped on several cores, the server's capacity
/// (admission of new rooms) and its drain (no new rooms, running ones finish, the rest is closed).
/// </summary>
[Collection(Tests.Gaming.Engine.CpuHeavyCollection.Name)]
public class RoomFleetTests
{
    private static readonly ServerNodeOptions FastDrain = new()
    {
        DrainTimeout = TimeSpan.FromSeconds(10),
        ForceStopGrace = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    // ------------------------------------------------------------------ parallel steps

    private sealed class PuckHarness
    {
        public readonly PuckGame Game = new();
        public readonly RoomHost<PuckSim, Walk, Player> Host;
        public readonly WorldCoordinator World;
        public readonly List<string> Humans = new();
        private int _seq;

        public PuckHarness(int workers)
        {
            Game.EndAfter = mode => mode switch { "short" => 200, "mid" => 420, _ => int.MaxValue };
            Host = new RoomHost<PuckSim, Walk, Player>(Game, Game, new RoomHostOptions { ReturnAfterEndSeconds = 1 }, 60)
                .UseScheduler(new StepScheduler(workers));
            World = new WorldCoordinator(new IWorldStepper[] { Host });
            // 12 rooms: humans and bots, three lengths (rooms end and go during the run).
            for (var r = 0; r < 12; r++)
            {
                var mode = (r % 3) switch { 0 => "short", 1 => "mid", _ => "long" };
                var m = Host.CreateRoom(Game.CreateSimulation(mode, null), mode, null, null, new RoomRules());
                for (var seat = 1; seat <= 4; seat++)
                {
                    var s = m.AddSeat(seat, seat <= 2 ? 0 : 1);
                    if (seat <= 2)
                    {
                        var who = $"r{r}p{seat}";
                        var session = Host.Connect("c-" + who, who, new Player(who, who.ToUpperInvariant()))!;
                        m.GiveToHuman(s, m.Join(who, session.Player, session.ClientId));
                        session.Room = m;
                        Host.Bind(who, m);
                        Humans.Add("c-" + who);
                    }
                    else m.GiveToBot(s);
                }
                Host.Start(m);
            }
        }

        /// <summary>Every human sends one input per step (a pattern of its own), then the frame runs.</summary>
        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                _seq++;
                for (var h = 0; h < Humans.Count; h++)
                    Host.SubmitInput(Humans[h], _seq, new Walk(((_seq / 7 + h) % 5) - 2));
                World.Step(1f / 60f);
            }
        }
    }

    [Fact]
    public void Rooms_stepped_on_several_cores_match_rooms_stepped_on_one_bit_for_bit()
    {
        var serial = new PuckHarness(workers: 1);
        var parallel = new PuckHarness(workers: 4);
        var serialHashes = new List<string>();
        var parallelHashes = new List<string>();
        for (var chunk = 0; chunk < 10; chunk++)
        {
            serial.Run(60);
            parallel.Run(60);
            serialHashes.Add(string.Join(",", serial.Host.Rooms.Select(m => $"{m.Mode}:{m.Sim.Hash():x}")));
            parallelHashes.Add(string.Join(",", parallel.Host.Rooms.Select(m => $"{m.Mode}:{m.Sim.Hash():x}")));
        }
        Assert.Equal(serialHashes, parallelHashes);

        // The same packets, in the same order, to every client (snapshots carry the state hash).
        foreach (var client in serial.Humans)
            Assert.Equal(serial.Game.To(client), parallel.Game.To(client));
        Assert.Equal(serial.Game.Out.Count, parallel.Game.Out.Count);
        Assert.Contains(serial.Game.To(serial.Humans[0]), p => p.StartsWith("menu:short"));

        // Rooms ended and went the same way; four long rooms remain.
        Assert.Equal(8, serial.Game.Finished.Count);
        Assert.Equal(8, parallel.Game.Finished.Count);
        Assert.Equal(4, parallel.Host.Rooms.Count);

        // The parallel host really used several threads for the simulations, never for packets.
        var simThreads = parallel.Host.Rooms.SelectMany(m => m.Sim.Threads.Keys).Distinct().Count();
        Assert.True(simThreads > 1, $"simulations ran on {simThreads} thread(s)");
        Assert.Single(parallel.Game.SendThreads);
        Assert.All(parallel.Host.Rooms, m => Assert.Equal(m.Sim.Steps, m.Sim.BeforeSteps));
    }

    [Fact]
    public void One_worker_keeps_every_room_on_the_engine_thread()
    {
        var h = new PuckHarness(workers: 1);
        h.Run(30);
        var engine = Environment.CurrentManagedThreadId;
        Assert.All(h.Host.Rooms, m => Assert.Equal(new[] { engine }, m.Sim.Threads.Keys));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void A_failing_room_is_reported_and_the_other_rooms_go_on(int workers)
    {
        var h = new PuckHarness(workers);
        h.Run(10);
        var broken = h.Host.Rooms[5];
        broken.Sim.ThrowAt = broken.Sim.Steps;
        var others = h.Host.Rooms.Where(m => m != broken).Select(m => (Room: m, Steps: m.Sim.Steps)).ToList();
        var outBefore = h.Game.Out.Count;
        var ex = Assert.Throws<AggregateException>(() => h.Host.FixedStep(new FixedStep(1, 1f / 60f, 0, 1)));
        Assert.Equal("sim boom", Assert.Single(ex.InnerExceptions).Message);
        Assert.All(others, o => Assert.Equal(o.Steps + 1, o.Room.Sim.Steps));
        // A room that keeps failing does not hold the others back either.
        Assert.Throws<AggregateException>(() => h.Host.FixedStep(new FixedStep(2, 1f / 60f, 0, 1)));
        Assert.All(others, o => Assert.Equal(o.Steps + 2, o.Room.Sim.Steps));
        // The step counted and the snapshot round still went out (every 2nd step).
        Assert.True(h.Game.Out.Count > outBefore);
        // Through the coordinator the fault is logged and the frames keep running.
        h.Run(5);
        Assert.All(others, o => Assert.Equal(o.Steps + 7, o.Room.Sim.Steps));
        broken.Sim.ThrowAt = -1;
        h.Run(1);
        Assert.Equal(others[0].Steps + 1, broken.Sim.Steps);
    }

    // ------------------------------------------------------------------ capacity

    private static readonly PlaylistOptions Duel = new()
    {
        Id = "duel",
        Mode = "duel",
        TeamSize = 1,
        Rules = new RoomRules { BotRefill = true, OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot },
    };

    private static readonly PlaylistOptions Backfill = new()
    {
        Id = "backfill",
        Mode = "duel",
        TeamSize = 1,
        Rules = new RoomRules { BotRefill = true, JoinInProgress = true, OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot },
    };

    private static readonly PlaylistOptions Ranked = new()
    {
        Id = "ranked",
        Mode = "duel",
        TeamSize = 1,
        Rated = true,
        Rules = new RoomRules(),
    };

    private sealed class Fleet
    {
        public readonly LineGame Game = new();
        public readonly ServerNode Node;
        public readonly RoomHost<LineSim, Walk, Player> Host;
        public readonly MatchmakingModule<LineSim, Walk, Player> Mm;
        public readonly LobbyModule<LineSim, Walk, Player> Lobbies;
        public readonly WorldCoordinator World;
        public ReadyState Status = ReadyState.Alive;

        public Fleet(double maxLoad, int endAfter = int.MaxValue, ServerNodeOptions? options = null)
        {
            Game.EndAfter = endAfter;
            Node = new ServerNode((options ?? FastDrain) with { MaxLoad = maxLoad }, readiness: () => Status, nodeId: "test-node");
            Host = new RoomHost<LineSim, Walk, Player>(Game, Game, new RoomHostOptions { ReturnAfterEndSeconds = 1 }, 60);
            Mm = new MatchmakingModule<LineSim, Walk, Player>(Game, new MatchmakingOptions(), new[] { Duel, Backfill, Ranked });
            Lobbies = new LobbyModule<LineSim, Walk, Player>(Game, new LobbyOptions());
            Host.Use(Mm).Use(Lobbies).UseServer(Node);
            World = new WorldCoordinator(new IWorldStepper[] { Host });
        }

        public RoomSession<LineSim, Walk, Player> Connect(string who, int rating = 1000) =>
            Host.Connect("c-" + who, who, new Player(who, who.ToUpperInvariant(), rating))!;

        public bool Queue(string who, PlaylistOptions playlist) => Mm.Enqueue(Host.SessionOf("c-" + who)!, playlist);

        public void Run(double seconds)
        {
            for (var i = 0; i < (int)Math.Round(seconds * 60); i++) World.Step(1f / 60f);
        }

        public List<string> To(string who) => Game.To("c-" + who);
    }

    [Fact]
    public void Rooms_report_their_count_and_load_to_the_server()
    {
        var f = new Fleet(maxLoad: 0);
        f.Game.Load = mode => mode == "big" ? 2.5 : 1;
        var duel = f.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules());
        Assert.Equal(0, f.Node.Capacity().UnitsOf("rooms")); // created, not started
        f.Host.Start(duel);
        var big = f.Host.CreateRoom(new LineSim(), "big", null, null, new RoomRules());
        f.Host.Start(big);
        Assert.Equal(2.5, big.Load);
        var c = f.Node.Capacity();
        Assert.Equal(2, c.UnitsOf("rooms"));
        Assert.Equal(3.5, c.Load, 6);
        f.Host.Dispose(big);
        Assert.Equal(1, f.Node.Capacity().UnitsOf("rooms"));
        Assert.Equal(1, f.Node.Capacity().Load, 6);
        Assert.Equal(new CapacitySample(1, 1), f.Host.Sample());
    }

    [Fact]
    public void Binding_the_host_twice_does_not_count_its_rooms_twice()
    {
        var f = new Fleet(maxLoad: 0);
        f.Host.UseServer(f.Node);
        f.Host.Start(f.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules()));
        Assert.Equal(1, f.Node.Capacity().UnitsOf("rooms"));
    }

    [Fact]
    public void A_host_without_a_server_opens_rooms_freely()
    {
        var game = new LineGame();
        var host = new RoomHost<LineSim, Walk, Player>(game, game, new RoomHostOptions(), 60);
        Assert.True(host.CanOpenRoom("duel", null));
        Assert.Null(host.Server);
    }

    [Fact]
    public void Matchmaking_opens_rooms_only_while_they_fit_and_goes_on_when_one_ends()
    {
        var f = new Fleet(maxLoad: 2, endAfter: 600); // a room ends after 10 s
        foreach (var who in new[] { "a", "b", "c", "d", "e", "f" })
        {
            f.Connect(who);
            Assert.True(f.Queue(who, Duel));
        }
        f.Run(0.5);
        Assert.Equal(2, f.Host.Rooms.Count);
        Assert.Equal(ServerNodeState.Full, f.Node.State);
        Assert.Equal(2, f.Mm.Queue.Waiting("duel").Count);
        // Still searching (told every second), not dropped.
        f.Run(2);
        Assert.Equal("queue:Searching:duel:2/2", f.To("e").Last());
        Assert.Equal(2, f.Host.Rooms.Count);

        // The first rooms end (10 s) and go (1 s later): the waiting pair gets a room.
        f.Run(9);
        Assert.Contains(f.To("e"), p => p == "queue:Found:duel:2/2");
        Assert.Empty(f.Mm.Queue.Waiting("duel"));
        Assert.Single(f.Host.Rooms);
        Assert.Equal(ServerNodeState.Ready, f.Node.State);
    }

    [Fact]
    public void Rated_groups_wait_for_room_too()
    {
        var f = new Fleet(maxLoad: 1);
        foreach (var who in new[] { "a", "b", "c", "d" })
        {
            f.Connect(who);
            f.Queue(who, Ranked);
        }
        f.Run(0.5);
        Assert.Single(f.Host.Rooms);
        Assert.Equal(2, f.Mm.Queue.Waiting("ranked").Count);
        f.Host.Dispose(f.Host.Rooms[0]);
        f.Run(0.1);
        Assert.Single(f.Host.Rooms);
        Assert.Empty(f.Mm.Queue.Waiting("ranked"));
    }

    [Fact]
    public void A_full_server_still_backfills_running_rooms()
    {
        var f = new Fleet(maxLoad: 1);
        f.Connect("a");
        f.Queue("a", Backfill);
        f.Run(6.5); // alone: filled with a bot after the wait
        var room = Assert.Single(f.Host.Rooms);
        Assert.Equal(ServerNodeState.Full, f.Node.State);
        // A newcomer takes the bot seat of the running room (no new room needed).
        f.Connect("b");
        f.Queue("b", Backfill);
        f.Run(3);
        Assert.Single(f.Host.Rooms);
        Assert.Contains(room.Seats, s => s.Owner?.PrincipalId == "b");
    }

    [Fact]
    public void A_server_that_is_not_ready_opens_no_rooms()
    {
        var f = new Fleet(maxLoad: 0) { Status = ReadyState.Starting };
        f.Connect("a");
        f.Connect("b");
        f.Queue("a", Duel);
        f.Queue("b", Duel);
        f.Run(1);
        Assert.Empty(f.Host.Rooms);
        f.Status = ReadyState.Alive;
        f.Run(0.1);
        Assert.Single(f.Host.Rooms);
    }

    [Fact]
    public void A_lobby_cannot_start_while_the_server_is_full()
    {
        var f = new Fleet(maxLoad: 1);
        var blocker = f.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules());
        f.Host.Start(blocker);
        var a = f.Connect("a");
        f.Lobbies.Join(a, "");
        f.Lobbies.Start(a, "duo");
        Assert.Equal("reject:Unavailable", f.To("a").Last());
        Assert.Single(f.Host.Rooms);
        f.Host.Dispose(blocker);
        f.Lobbies.Start(a, "duo");
        Assert.Single(f.Host.Rooms);
        Assert.Equal(f.Lobbies.LobbyOf(a)!.Code, f.Host.Rooms[0].LobbyCode);
    }

    // ------------------------------------------------------------------ drain

    [Fact]
    public async Task A_drain_closes_queues_and_lobbies_and_lets_the_running_rooms_finish()
    {
        var f = new Fleet(maxLoad: 0, endAfter: 300); // rooms end after 5 s
        f.Connect("a");
        f.Connect("b");
        f.Queue("a", Duel);
        f.Queue("b", Duel);
        f.Run(0.2);
        var room = Assert.Single(f.Host.Rooms);
        f.Connect("c");
        f.Queue("c", Duel);
        var d = f.Connect("d");
        f.Lobbies.Join(d, "");
        f.Connect("e");

        var drain = f.Node.DrainAsync();
        Assert.True(f.Host.IsDraining);
        Assert.False(f.Host.CanOpenRoom("duel", "duel"));
        Assert.Equal(ServerNodeState.Draining, f.Node.State);
        f.Run(1f / 60f);

        // Everyone without a room is told; the queue is empty; the players in the room are not disturbed.
        Assert.Equal(new[] { "queue:Idle:duel:0/0", "draining:" }, f.To("c").TakeLast(2));
        Assert.Equal("draining:", f.To("d").Last());
        Assert.Equal("draining:", f.To("e").Last());
        Assert.DoesNotContain("draining:", f.To("a"));
        Assert.Empty(f.Mm.Queue.Waiting("duel"));

        // Nothing new is taken.
        Assert.False(f.Queue("e", Duel));
        Assert.Equal(new[] { "queue:Idle:duel:0/0", "draining:" }, f.To("e").TakeLast(2));
        f.Lobbies.Join(f.Connect("g"), "");
        Assert.Equal("reject:Unavailable", f.To("g").Last());
        f.Lobbies.Start(d, "duo");
        Assert.Equal("reject:Unavailable", f.To("d").Last());
        Assert.Single(f.Host.Rooms);

        // The room plays on to its end; then its players go back to the menu and are told too.
        f.Run(2);
        Assert.False(drain.IsCompleted);
        Assert.False(f.Host.IsDrained);
        f.Run(4.5);
        Assert.Empty(f.Host.Rooms);
        Assert.Contains(room.Id, f.Game.Finished);
        Assert.Empty(f.Game.ForcedClosed);
        Assert.Equal(new[] { "menu:duel", "draining:" }, f.To("a").TakeLast(2));
        Assert.True(f.Host.IsDrained);
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ServerNodeState.Drained, f.Node.State);
    }

    [Fact]
    public void A_player_may_still_rejoin_their_room_during_a_drain_but_a_newcomer_is_sent_away()
    {
        var f = new Fleet(maxLoad: 0);
        f.Connect("a");
        f.Connect("b");
        f.Queue("a", Duel);
        f.Queue("b", Duel);
        f.Run(0.2);
        var room = Assert.Single(f.Host.Rooms);
        f.Host.Disconnect("c-a");
        _ = f.Node.DrainAsync();
        f.Run(0.1);

        var back = f.Host.Connect("c-a2", "a", new Player("a", "A"))!;
        Assert.Same(room, back.Room);
        Assert.Contains("welcome:1", f.Game.To("c-a2"));
        Assert.DoesNotContain("draining:", f.Game.To("c-a2"));

        f.Connect("z");
        Assert.Equal(new[] { "draining:" }, f.To("z"));
    }

    [Fact]
    public void A_pending_join_is_called_off_when_the_drain_begins()
    {
        var f = new Fleet(maxLoad: 0);
        f.Connect("a");
        f.Queue("a", Backfill);
        f.Run(6.5);
        var room = Assert.Single(f.Host.Rooms);
        f.Connect("b");
        f.Queue("b", Backfill);
        f.Run(0.1); // b is about to take the bot seat (notice running)
        var b = f.Host.SessionOf("c-b")!;
        Assert.Same(room, b.PendingRoom);

        _ = f.Node.DrainAsync();
        f.Run(1f / 60f);
        Assert.Null(b.PendingRoom);
        Assert.All(room.Seats, s => Assert.Null(s.Pending));
        Assert.Equal(new[] { "queue:Idle:backfill:0/0", "draining:" }, f.To("b").TakeLast(2));
        Assert.Contains("notice:Left:2:B", f.To("a"));
        f.Run(3);
        Assert.DoesNotContain(room.Seats, s => s.Owner?.PrincipalId == "b");
    }

    [Fact]
    public async Task A_timed_out_drain_closes_the_rooms_that_still_run()
    {
        var f = new Fleet(maxLoad: 0, options: FastDrain with { DrainTimeout = TimeSpan.FromMilliseconds(150) });
        foreach (var who in new[] { "a", "b", "c", "d" })
        {
            f.Connect(who);
            f.Queue(who, Duel);
        }
        f.Run(0.2);
        Assert.Equal(2, f.Host.Rooms.Count);
        var ids = f.Host.Rooms.Select(m => m.Id).ToList();

        // The engine runs on its own thread from here on; the test only talks to the node.
        using var stop = new CancellationTokenSource();
        var engine = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                f.World.Step(1f / 60f);
                Thread.Sleep(1);
            }
        });
        try
        {
            Assert.False(await f.Node.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            stop.Cancel();
            await engine;
        }
        Assert.Equal(ids.OrderBy(x => x), f.Game.ForcedClosed.OrderBy(x => x));
        Assert.Empty(f.Game.Finished);
        Assert.Empty(f.Host.Rooms);
        Assert.Equal(new[] { "menu:duel", "draining:" }, f.To("a").TakeLast(2));
        Assert.True(f.Node.WasForced);
        Assert.Equal(ServerNodeState.Drained, f.Node.State);
    }

    [Fact]
    public void A_room_that_already_ended_is_not_reported_as_force_closed()
    {
        var f = new Fleet(maxLoad: 0, endAfter: 30);
        f.Connect("a");
        f.Connect("b");
        f.Queue("a", Duel);
        f.Queue("b", Duel);
        f.Run(0.7); // ended (0.5 s), not yet gone (1 s after the end)
        var room = Assert.Single(f.Host.Rooms);
        Assert.True(room.Finished);
        f.Host.ForceStop();
        f.Run(1f / 60f);
        Assert.Empty(f.Host.Rooms);
        Assert.Empty(f.Game.ForcedClosed);
    }

    [Fact]
    public async Task Rooms_on_several_cores_drain_on_a_live_engine_thread()
    {
        // The whole flow: a parallel host bound to the node, stepped by a background engine; the
        // drain waits for the rooms to end on their own.
        var game = new PuckGame { EndAfter = mode => mode == "short" ? 90 : 240 };
        using var node = new ServerNode(FastDrain with { MaxLoad = 20 }, nodeId: "live");
        var host = new RoomHost<PuckSim, Walk, Player>(game, game, new RoomHostOptions { ReturnAfterEndSeconds = 0.2 }, 60)
            .UseScheduler(new StepScheduler(4))
            .UseServer(node);
        for (var r = 0; r < 8; r++)
        {
            var mode = r % 2 == 0 ? "short" : "long";
            var m = host.CreateRoom(game.CreateSimulation(mode, null), mode, null, null, new RoomRules());
            for (var seat = 1; seat <= 4; seat++) m.GiveToBot(m.AddSeat(seat, seat <= 2 ? 0 : 1));
            host.Start(m);
        }
        Assert.Equal(8, node.Capacity().UnitsOf("rooms"));
        Assert.Equal(8, node.Capacity().Load, 6);
        Assert.True(node.CanAccept(12));
        Assert.False(node.CanAccept(12.5));

        var world = new WorldCoordinator(new IWorldStepper[] { host });
        using var stop = new CancellationTokenSource();
        var engine = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                world.Step(1f / 60f);
                Thread.Sleep(1);
            }
        });
        try
        {
            Assert.True(await node.DrainAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            stop.Cancel();
            await engine;
        }
        Assert.Equal(8, game.Finished.Count);
        Assert.Empty(game.ForcedClosed);
        Assert.Equal(0, node.Capacity().UnitsOf("rooms"));
        Assert.Equal(ServerNodeState.Drained, node.State);
    }
}
