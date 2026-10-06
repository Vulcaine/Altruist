/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.Rooms;

/// <summary>
/// Hosts fixed-step, authoritative rooms: one connection per principal (the newest wins),
/// rooms with seats driven by players or bots, sequenced inputs, snapshots on a global step
/// phase, reconnect grace and rejoin, bot refills, the room's end and disposal. Matchmaking and
/// lobbies plug in as <see cref="IRoomHostModule{TSim,TInput,TPlayer}"/>s.
/// <para>
/// A fixed-rate <see cref="IWorldStepper"/>: the engine's world coordinator calls
/// <see cref="BeforeSteps"/>, <see cref="FixedStep"/> × N and <see cref="AfterSteps"/> every frame.
/// Everything, including the connection calls below, runs on the engine thread (config
/// <c>altruist:game:engine:world-step: inline</c>; post from sockets with <c>WaitForNextTick</c>).
/// With <see cref="UseScheduler"/> (more than one worker) the rooms' simulation steps run in
/// parallel inside <see cref="FixedStep"/>; everything else stays on the engine thread.
/// </para>
/// <para>
/// Fleet: <see cref="UseServer"/> binds the host to the <see cref="IServerNode"/>: its rooms count
/// against the server's capacity (kind <c>rooms</c>, load <see cref="IRoomGame{TSim,TInput,TPlayer}.LoadOf"/>),
/// matchmaking and lobbies open rooms only while it fits, and a drain stops new rooms, lets the
/// running ones end and finally closes what is left.
/// </para>
/// </summary>
public class RoomHost<TSim, TInput, TPlayer> : IWorldStepper, ICapacityContributor, IDrainParticipant
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    private readonly IRoomGame<TSim, TInput, TPlayer> _game;
    private readonly IRoomTransport _transport;
    private readonly Func<DateTime> _utcNow;
    private readonly int _fixedHz;
    private readonly float _fixedDt;
    private readonly List<IRoomHostModule<TSim, TInput, TPlayer>> _modules = new();

    private readonly Dictionary<string, RoomSession<TSim, TInput, TPlayer>> _byClient = new();
    private readonly Dictionary<string, RoomSession<TSim, TInput, TPlayer>> _byPrincipal = new();
    private readonly List<Room<TSim, TInput, TPlayer>> _rooms = new();
    /// <summary>Principals that may still (re)join a live room.</summary>
    private readonly Dictionary<string, Room<TSim, TInput, TPlayer>> _roomOfPrincipal = new();

    private double _now;
    private int _stepCounter;

    private IStepScheduler? _scheduler;
    private IServerNode? _node;
    private readonly List<IDisposable> _nodeRegistrations = new();
    private readonly object _sampleGate = new();
    private CapacitySample _sample = CapacitySample.Empty;
    private volatile bool _drainRequested;
    private volatile bool _forceRequested;
    private volatile bool _drainHandled;
    private bool _forced;
    private static readonly Action<Room<TSim, TInput, TPlayer>> StepRoom = static m => m.Step();

    private IFleet? _fleet;
    private readonly ConcurrentQueue<Action> _posted = new();
    private int _pendingAsync;
    /// <summary>Connections whose arrival lookup (room elsewhere, handed-over work) is still running.</summary>
    private readonly Dictionary<string, bool> _arriving = new();

    public RoomHost(IRoomGame<TSim, TInput, TPlayer> game, IRoomTransport transport, RoomHostOptions options, int fixedHz,
        Func<DateTime>? utcNow = null)
    {
        _game = game;
        _transport = transport;
        Options = options;
        _fixedHz = fixedHz;
        _fixedDt = 1f / fixedHz;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public RoomHostOptions Options { get; }
    public IRoomGame<TSim, TInput, TPlayer> Game => _game;
    /// <summary>Host time in seconds (advances with the frames, clamped like the steps).</summary>
    public double Now => _now;
    public DateTime UtcNow => _utcNow();
    /// <summary>Fixed steps taken so far.</summary>
    public int CurrentStep => _stepCounter;
    public IReadOnlyList<Room<TSim, TInput, TPlayer>> Rooms => _rooms;
    public int SessionCount => _byClient.Count;

    public RoomSession<TSim, TInput, TPlayer>? SessionOf(string clientId) => _byClient.GetValueOrDefault(clientId);

    public RoomSession<TSim, TInput, TPlayer>? SessionOfPrincipal(string principalId) => _byPrincipal.GetValueOrDefault(principalId);

    public bool Contains(Room<TSim, TInput, TPlayer> room) => _rooms.Contains(room);

    /// <summary>Adds a module; hooks run in the order modules were added.</summary>
    public RoomHost<TSim, TInput, TPlayer> Use(IRoomHostModule<TSim, TInput, TPlayer> module)
    {
        _modules.Add(module);
        module.Attach(this);
        return this;
    }

    public T? Module<T>() where T : class => _modules.OfType<T>().FirstOrDefault();

    /// <summary>
    /// A module takes the connection for new work (it queues, it joins a lobby): every other
    /// module lets go of it (<see cref="IRoomHostModule{TSim,TInput,TPlayer}.OnTakenOver"/>).
    /// </summary>
    public void TakeOver(RoomSession<TSim, TInput, TPlayer> s, IRoomHostModule<TSim, TInput, TPlayer> by)
    {
        foreach (var mod in _modules)
            if (mod != by) mod.OnTakenOver(s);
    }

    // ------------------------------------------------------------------ fleet: cores, capacity, drain

    /// <summary>Steps the rooms' simulations with this scheduler (more than one worker: in parallel).</summary>
    public RoomHost<TSim, TInput, TPlayer> UseScheduler(IStepScheduler scheduler)
    {
        _scheduler = scheduler;
        return this;
    }

    public IStepScheduler? Scheduler => _scheduler;

    /// <summary>Counts the rooms against the server's capacity and takes part in its drain.</summary>
    public RoomHost<TSim, TInput, TPlayer> UseServer(IServerNode node)
    {
        foreach (var r in _nodeRegistrations) r.Dispose();
        _nodeRegistrations.Clear();
        _node = node;
        _nodeRegistrations.Add(node.Register((ICapacityContributor)this));
        _nodeRegistrations.Add(node.Register((IDrainParticipant)this));
        return this;
    }

    public IServerNode? Server => _node;

    /// <summary>
    /// Makes the host a member of the fleet: rooms are found from any server (a player connecting
    /// elsewhere is sent to its room), players carry their queue place or lobby when they move,
    /// and the modules spread their work over the servers. Needs <see cref="IRoomGame{TSim,TInput,TPlayer}.Redirect"/>
    /// to move players; with one server (or a backplane nobody else shares) nothing changes.
    /// </summary>
    public RoomHost<TSim, TInput, TPlayer> UseFleet(IFleet fleet)
    {
        _fleet = fleet;
        foreach (var principal in _roomOfPrincipal.Keys) fleet.Claim(PlayerUnit, principal);
        return this;
    }

    public IFleet? Fleet => _fleet;

    /// <summary>The fleet unit kind under which a player's room is claimed.</summary>
    public const string PlayerUnit = "player";

    /// <summary>Other servers may exist (the fleet's backplane is shared).</summary>
    public bool FleetShared => _fleet is { Shared: true };

    /// <summary>Runs <paramref name="action"/> on the engine thread at the start of the next frame's <see cref="AfterSteps"/> (any thread).</summary>
    public void Post(Action action) => _posted.Enqueue(action);

    /// <summary>
    /// Runs I/O off the engine thread and continues with its result on it (next frame). A failure
    /// goes to <paramref name="failed"/> (on the engine thread) or is dropped.
    /// </summary>
    public void RunAsync<T>(Func<Task<T>> work, Action<T> then, Action<Exception>? failed = null)
    {
        Interlocked.Increment(ref _pendingAsync);
        Task.Run(async () =>
        {
            try
            {
                var result = await work().ConfigureAwait(false);
                Post(() => then(result));
            }
            catch (Exception ex)
            {
                if (failed is not null) Post(() => failed(ex));
            }
            finally
            {
                Interlocked.Decrement(ref _pendingAsync);
            }
        });
    }

    /// <summary>Async work started with <see cref="RunAsync"/> that has not posted back yet, plus posted actions not run yet.</summary>
    public int PendingAsync => Volatile.Read(ref _pendingAsync) + _posted.Count;

    private void RunPosted()
    {
        // Only what was posted before this frame: work posted by these actions waits for the next one.
        for (var n = _posted.Count; n > 0 && _posted.TryDequeue(out var action); n--) action();
    }

    private void BindPrincipal(string principalId, Room<TSim, TInput, TPlayer> room)
    {
        _roomOfPrincipal[principalId] = room;
        _fleet?.Claim(PlayerUnit, principalId);
    }

    private void UnbindPrincipal(string principalId)
    {
        if (_roomOfPrincipal.Remove(principalId)) _fleet?.Release(PlayerUnit, principalId);
    }

    /// <summary>
    /// Sends the connection to another server, with work handed over (written before the client is
    /// told). False when the game has no redirect packet or the server is unknown. The connection
    /// is closed once the packet went out; its local state goes as for any disconnect.
    /// </summary>
    public bool Redirect(RoomSession<TSim, TInput, TPlayer> s, string nodeId, string reason, FleetHandoff? handoff = null, Action? undo = null)
    {
        if (_fleet is not { } fleet || nodeId == fleet.NodeId || fleet.Find(nodeId) is not { } node) return false;
        var packet = _game.Redirect(new RoomRedirect(node.NodeId, fleet.NodeParam, node.PublicAddress, reason));
        if (packet is null) return false;
        s.Set(new Leaving());
        RunAsync(async () =>
        {
            if (handoff is not null) await fleet.PutHandoffAsync(node.NodeId, s.PrincipalId, handoff).ConfigureAwait(false);
            return true;
        }, _ =>
        {
            if (_byClient.GetValueOrDefault(s.ClientId) != s) return;
            Send(s.ClientId, packet);
            DropSession(s);
            _transport.Disconnect(s.ClientId);
        }, _ =>
        {
            // The handover could not be written: the player stays here.
            s.Set<Leaving>(null);
            undo?.Invoke();
        });
        return true;
    }

    /// <summary>The connection is being sent to another server (its work is no longer this server's).</summary>
    public static bool IsLeaving(RoomSession<TSim, TInput, TPlayer> s) => s.Get<Leaving>() is not null;

    private sealed class Leaving;

    private sealed class ToldDraining;

    /// <summary>Tells a connection once that this server is going away.</summary>
    private void TellDraining(RoomSession<TSim, TInput, TPlayer> s)
    {
        if (s.Get<ToldDraining>() is not null) return;
        s.Set(new ToldDraining());
        Send(s.ClientId, _game.ServerDraining());
    }

    /// <summary>The best other server that takes new work of this cost (lowest load first), or null.</summary>
    public FleetNodeInfo? OtherServerFor(double cost)
    {
        if (_fleet is not { } fleet) return null;
        FleetNodeInfo? best = null;
        foreach (var n in fleet.Nodes)
        {
            if (n.NodeId == fleet.NodeId || !n.Fits(cost)) continue;
            if (best is null || n.Load < best.Load || (n.Load == best.Load && string.CompareOrdinal(n.NodeId, best.NodeId) < 0)) best = n;
        }
        return best;
    }

    /// <summary>
    /// A connection without a room here: is its room on another server, or was work handed to it?
    /// Looked up off the engine thread; a rejoin request waits for the answer.
    /// </summary>
    private void Arrive(RoomSession<TSim, TInput, TPlayer> s)
    {
        var fleet = _fleet!;
        _arriving[s.ClientId] = false;
        RunAsync(async () =>
        {
            var handoff = await fleet.TakeHandoffAsync(s.PrincipalId).ConfigureAwait(false);
            var owner = await fleet.LocateAsync(PlayerUnit, s.PrincipalId).ConfigureAwait(false);
            return (handoff, owner);
        }, r => Arrived(s, r.handoff, r.owner), _ => Arrived(s, null, null));
    }

    private void Arrived(RoomSession<TSim, TInput, TPlayer> s, FleetHandoff? handoff, string? owner)
    {
        var askedRejoin = _arriving.Remove(s.ClientId, out var asked) && asked;
        if (_byClient.GetValueOrDefault(s.ClientId) != s || IsLeaving(s))
        {
            // Gone, replaced or already sent elsewhere meanwhile: keep the handed-over work for its next connection.
            if (handoff is not null && _fleet is { } f) RunAsync(async () => { await f.PutHandoffAsync(f.NodeId, s.PrincipalId, handoff).ConfigureAwait(false); return true; }, _ => { });
            return;
        }
        if (s.Room is null && owner is not null && owner != _fleet!.NodeId && Redirect(s, owner, "rejoin"))
            return;
        if (handoff is not null && s.Room is null)
        {
            var taken = false;
            foreach (var mod in _modules)
                if (mod.OnHandoff(s, handoff)) { taken = true; break; }
            if (taken) return;
        }
        if (_drainHandled && s.Room is null)
        {
            if (OtherServerFor(1) is { } other && Redirect(s, other.NodeId, "drain")) return;
            TellDraining(s);
            return;
        }
        if (askedRejoin) RejoinRequest(s.ClientId);
    }

    /// <summary>The server is draining: no new rooms, queues and lobbies are closed.</summary>
    public bool IsDraining => _drainRequested;

    /// <summary>A room of this mode may open now: not draining and its load fits the server's capacity.</summary>
    public bool CanOpenRoom(string mode, string? playlist) =>
        !_drainRequested && (_node?.CanAccept(_game.LoadOf(mode, playlist)) ?? true);

    string ICapacityContributor.Kind => "rooms";
    string IDrainParticipant.Kind => "rooms";

    /// <summary>Rooms and their load (thread-safe; refreshed on the engine thread when rooms start or go).</summary>
    public CapacitySample Sample()
    {
        lock (_sampleGate)
            return _sample;
    }

    private void RefreshSample()
    {
        double load = 0;
        foreach (var m in _rooms) load += m.Load;
        lock (_sampleGate)
            _sample = new CapacitySample(_rooms.Count, load);
    }

    /// <summary>Any thread: the next frame stops new rooms and closes queues and lobbies.</summary>
    public void BeginDrain() => _drainRequested = true;

    /// <summary>The drain was handled and no room runs any more.</summary>
    public bool IsDrained => _drainHandled && Sample().Units == 0;

    /// <summary>Any thread: the next frame closes every room still running.</summary>
    public void ForceStop()
    {
        _drainRequested = true;
        _forceRequested = true;
    }

    private void RunDrain()
    {
        if (_drainRequested && !_drainHandled)
        {
            foreach (var mod in _modules) mod.OnDraining();
            foreach (var s in _byClient.Values.ToArray())
                if (s.Room is null && !IsLeaving(s)) TellDraining(s);
            _drainHandled = true;
        }
        if (_forceRequested && !_forced)
        {
            _forced = true;
            foreach (var m in _rooms.ToArray())
            {
                if (!m.Finished) _game.OnForcedClose(m);
                Dispose(m);
            }
        }
    }

    // ------------------------------------------------------------------ world stepper

    public StepMode Mode => StepMode.Fixed;
    public int FixedHz => _fixedHz;

    /// <summary>Advances host time by the frame's (clamped) duration, before its fixed steps.</summary>
    public virtual void BeforeSteps(in FrameInfo frame) => _now += frame.Dt;

    /// <summary>
    /// One step of every room; snapshots every <see cref="RoomHostOptions.SnapshotEveryNSteps"/> steps.
    /// With a multi-worker scheduler the simulations step in parallel first, then the ends are
    /// handled on this thread in room order (each client sees the same packets in the same order).
    /// A room that throws does not stop the others: every room steps, the snapshots go out, and the
    /// failures are thrown together at the end (the coordinator logs them).
    /// </summary>
    public virtual void FixedStep(in FixedStep step)
    {
        AggregateException? failure = null;
        if (_scheduler is { Workers: > 1 } scheduler && _rooms.Count > 1)
        {
            var rooms = _rooms.ToArray();
            try
            { scheduler.ForEach(rooms, StepRoom); }
            catch (AggregateException ex) { failure = ex; }
            foreach (var m in rooms)
            {
                if (m.Sim.Ended && !m.Finished) Finish(m);
                if (m.EndedSeconds > Options.ReturnAfterEndSeconds) Dispose(m);
            }
        }
        else
        {
            List<Exception>? errors = null;
            foreach (var m in _rooms.ToArray())
            {
                try
                { m.Step(); }
                catch (Exception ex) { (errors ??= new()).Add(ex); }
                if (m.Sim.Ended && !m.Finished) Finish(m);
                if (m.EndedSeconds > Options.ReturnAfterEndSeconds) Dispose(m);
            }
            if (errors is not null) failure = new AggregateException(errors);
        }
        _stepCounter++;
        if (_stepCounter % Options.SnapshotEveryNSteps == 0)
            foreach (var m in _rooms) SendSnapshots(m);
        // A failing room is reported (the coordinator logs it) after the others went on.
        if (failure is not null) throw failure;
    }

    /// <summary>Once per frame after the steps: drain, reconnect timers, modules (matchmaking), bot refills, modules.</summary>
    public virtual void AfterSteps(in FrameInfo frame)
    {
        RunPosted();
        RunDrain();
        RunGraceTimers();
        foreach (var mod in _modules) mod.BeforeBotRefills();
        RunBotRefills();
        foreach (var mod in _modules) mod.AfterFrame();
    }

    public float FixedDt => _fixedDt;

    // ------------------------------------------------------------------ connections

    /// <summary>
    /// An authenticated connection opened. A previous connection of the principal is replaced
    /// (told, dropped, closed); a principal with a live room is seated again when the rules allow.
    /// </summary>
    public RoomSession<TSim, TInput, TPlayer>? Connect(string clientId, string principalId, TPlayer player)
    {
        if (_byClient.ContainsKey(clientId)) return null;
        if (_byPrincipal.TryGetValue(principalId, out var old))
        {
            _game.OnSessionReplaced(old);
            DropSession(old);
            _transport.Disconnect(old.ClientId);
        }
        var s = new RoomSession<TSim, TInput, TPlayer> { ClientId = clientId, PrincipalId = principalId, Player = player };
        _byClient[clientId] = s;
        _byPrincipal[principalId] = s;
        if (_roomOfPrincipal.TryGetValue(principalId, out var m) && !TryRejoin(s, m))
            UnbindPrincipal(principalId);
        if (s.Room is null && FleetShared) Arrive(s);
        // A draining server still takes its players back into their rooms, nothing new.
        else if (_drainHandled && s.Room is null) TellDraining(s);
        return s;
    }

    /// <summary>The connection closed: it leaves queues and lobbies; a room keeps the seat for the grace.</summary>
    public void Disconnect(string clientId)
    {
        if (_byClient.TryGetValue(clientId, out var s)) DropSession(s);
    }

    private void DropSession(RoomSession<TSim, TInput, TPlayer> s)
    {
        _arriving.Remove(s.ClientId);
        _byClient.Remove(s.ClientId);
        if (_byPrincipal.GetValueOrDefault(s.PrincipalId) == s) _byPrincipal.Remove(s.PrincipalId);
        var room = s.Room;
        foreach (var mod in _modules) mod.OnSessionDropping(s);
        if (room is not null && room.Participants.TryGetValue(s.PrincipalId, out var p) && p.ClientId == s.ClientId)
        {
            s.Room = null;
            ParticipantLeft(room, p, voluntary: false);
        }
    }

    /// <summary>A sequenced input of the connection's seat.</summary>
    public void SubmitInput(string clientId, int seq, in TInput input)
    {
        if (!_byClient.TryGetValue(clientId, out var s)) return;
        if (s.Room is { } m && m.Participants.TryGetValue(s.PrincipalId, out var p) && p.ClientId == s.ClientId && p.Seat is not null)
            p.Input.Offer(seq, input);
    }

    /// <summary>The player of a connection changed (profile, cosmetics).</summary>
    public void UpdatePlayer(string clientId, TPlayer player)
    {
        if (!_byClient.TryGetValue(clientId, out var s)) return;
        s.Player = player;
        foreach (var mod in _modules) mod.OnPlayerUpdated(s);
    }

    /// <summary>The connection leaves its room on purpose (back to the menu or its lobby).</summary>
    public void Return(string clientId)
    {
        if (!_byClient.TryGetValue(clientId, out var s)) return;
        var m = s.Room;
        if (m is null || !m.Participants.TryGetValue(s.PrincipalId, out var p)) return;
        foreach (var mod in _modules)
            if (mod.TryReturn(s, m, p)) return;
        s.Room = null;
        if (m.Ended)
        {
            // Results are already final; just release the player.
            UnbindPrincipal(s.PrincipalId);
            p.ClientId = null;
        }
        else ParticipantLeft(m, p, voluntary: true);
        Send(s.ClientId, _game.ReturnedToMenu(m));
    }

    /// <summary>
    /// A rejoining connection asks for its room. The connect already seated it if it could, so a
    /// connection with nothing going on is told right away.
    /// </summary>
    public void RejoinRequest(string clientId)
    {
        if (!_byClient.TryGetValue(clientId, out var s)) return;
        if (_arriving.ContainsKey(clientId))
        {
            // Answered once the fleet lookup is back (it may move the connection to its room).
            _arriving[clientId] = true;
            return;
        }
        if (s.Room is not null || s.PendingRoom is not null) return;
        foreach (var mod in _modules)
            if (mod.IsBusy(s)) return;
        Send(s.ClientId, _game.NothingToRejoin());
    }

    // ------------------------------------------------------------------ rooms

    /// <summary>A new room (not running yet): add seats, then <see cref="Start"/>.</summary>
    public Room<TSim, TInput, TPlayer> CreateRoom(TSim sim, string mode, string? playlist, string? lobbyCode, RoomRules rules)
    {
        var room = new Room<TSim, TInput, TPlayer>(_game, Options, sim, mode, playlist, lobbyCode, rules, _utcNow())
        {
            Load = Math.Max(0, _game.LoadOf(mode, playlist)),
        };
        _game.OnRoomCreated(room);
        return room;
    }

    /// <summary>
    /// Starts the room when the server takes it (<see cref="CanOpenRoom"/>: not draining, its load
    /// fits); false leaves it unstarted (drop it). Matchmaking and lobbies start rooms this way.
    /// </summary>
    public bool TryStart(Room<TSim, TInput, TPlayer> room)
    {
        if (_rooms.Contains(room) || _drainRequested || !(_node?.CanAccept(room.Load) ?? true)) return false;
        Start(room);
        return true;
    }

    /// <summary>
    /// The room starts stepping (the game resets its simulation in <c>OnStarting</c>), whatever the
    /// server's capacity: use <see cref="TryStart"/> to respect it.
    /// </summary>
    public void Start(Room<TSim, TInput, TPlayer> room)
    {
        if (_rooms.Contains(room)) return;
        _game.OnStarting(room);
        _rooms.Add(room);
        RefreshSample();
    }

    /// <summary>The principal's seat in <paramref name="room"/> may be rejoined from now on.</summary>
    public void Bind(string principalId, Room<TSim, TInput, TPlayer> room) => BindPrincipal(principalId, room);

    public void Unbind(string principalId) => UnbindPrincipal(principalId);

    public Room<TSim, TInput, TPlayer>? BoundRoom(string principalId) => _roomOfPrincipal.GetValueOrDefault(principalId);

    /// <summary>A participant's connection left the room (closed, replaced or returned).</summary>
    public void ParticipantLeft(Room<TSim, TInput, TPlayer> m, Participant<TSim, TInput, TPlayer> p, bool voluntary)
    {
        p.ClientId = null;
        p.DisconnectedAt = _now;
        p.Input.Reset();
        if (m.Ended || !_rooms.Contains(m)) return;
        var seat = p.Seat;
        if (seat is not null) Notify(m, RoomNoticeKind.Left, seat.Id, _game.NameOf(p.Player));
        if (voluntary)
        {
            // Leaving on purpose forfeits the right to rejoin.
            p.LeftEarly = true;
            UnbindPrincipal(p.PrincipalId);
            if (seat is not null)
            {
                switch (m.Rules.OnVoluntaryLeave)
                {
                    case VoluntaryLeaveAction.Abandon: Abandon(m, p); break;
                    case VoluntaryLeaveAction.OpenForBot: OpenSeat(m, seat, _now + Options.BotRefillDelaySeconds); break;
                    default: m.RemoveSeat(seat); break;
                }
            }
        }
        if (!CheckEmpty(m)) SendRoster(m);
    }

    /// <summary>The owner is gone: the seat waits for a claim until <paramref name="botAt"/>, then a bot takes it.</summary>
    public void OpenSeat(Room<TSim, TInput, TPlayer> m, Seat<TSim, TInput, TPlayer> seat, double botAt)
    {
        if (seat.Owner is { } owner) owner.LeftEarly = true;
        m.Release(seat);
        seat.OpenSince = _now;
        seat.BotAt = botAt;
        m.Sim.SetBotControlled(seat.Id, true);
    }

    private void Abandon(Room<TSim, TInput, TPlayer> m, Participant<TSim, TInput, TPlayer> p)
    {
        p.Abandoned = true;
        p.LeftEarly = true;
        UnbindPrincipal(p.PrincipalId);
        if (p.Seat is not null) m.RemoveSeat(p.Seat);
        _game.OnAbandoned(m, p);
    }

    private void RunGraceTimers()
    {
        var grace = Options.ReconnectGraceSeconds;
        foreach (var m in _rooms.ToArray())
        {
            if (m.Ended) continue;
            // Runs every frame: copy the participants only when someone is disconnected.
            var anyDropped = false;
            foreach (var p in m.Participants.Values)
                if (!p.Connected && p.DisconnectedAt is not null) { anyDropped = true; break; }
            if (!anyDropped) continue;
            var changed = false;
            foreach (var p in m.Participants.Values.ToArray())
            {
                if (p.Connected || p.DisconnectedAt is not { } at) continue;
                var since = _now - at;
                if (m.Rules.OnDisconnect == DisconnectAction.BotAfterDelay)
                {
                    // A bot takes the seat after the refill delay; the player may still reclaim a team seat.
                    if (p.Seat is { } seat && since >= Options.BotRefillDelaySeconds)
                    {
                        OpenSeat(m, seat, _now);
                        changed = true;
                    }
                    if (since >= grace && _roomOfPrincipal.GetValueOrDefault(p.PrincipalId) == m)
                    {
                        // The seat went to a bot meanwhile: it stays (ForfeitRejoin) or leaves with the player.
                        var left = p.Seat ?? m.SeatOf(p.LastSeat);
                        if (left is not null && (left.Owner is not null && left.Owner != p || left.Pending is not null)) left = null;
                        ExpireGrace(m, p, left);
                        changed = true; // the room may now be empty
                    }
                }
                else if (p.Seat is { } held && since >= grace)
                {
                    ExpireGrace(m, p, held);
                    changed = true;
                }
            }
            if (changed && !CheckEmpty(m)) SendRoster(m);
        }
    }

    /// <summary>The grace of a disconnected player ran out: <see cref="RoomRules.OnGraceExpired"/> decides what happens to its seat.</summary>
    private void ExpireGrace(Room<TSim, TInput, TPlayer> m, Participant<TSim, TInput, TPlayer> p, Seat<TSim, TInput, TPlayer>? seat)
    {
        p.LeftEarly = true;
        UnbindPrincipal(p.PrincipalId);
        switch (m.Rules.OnGraceExpired)
        {
            case GraceExpiredAction.Abandon:
                Abandon(m, p);
                if (seat is not null && m.Seats.Contains(seat)) m.RemoveSeat(seat);
                break;
            case GraceExpiredAction.RemoveSeat:
                if (seat is not null) m.RemoveSeat(seat);
                break;
            default:
                // A held seat opens for a bot now (a seat a bot already drives keeps it).
                if (seat is not null && seat.Owner == p) OpenSeat(m, seat, _now);
                break;
        }
    }

    private void RunBotRefills()
    {
        foreach (var m in _rooms)
        {
            if (!m.Rules.BotRefill || m.Ended) continue;
            var changed = false;
            foreach (var seat in m.Seats)
            {
                if (!seat.IsOpen || seat.Pending is not null || seat.BotAt is not { } at || _now < at) continue;
                m.GiveToBot(seat);
                changed = true;
                Notify(m, RoomNoticeKind.BotIn, seat.Id, _game.BotNameOf(m, seat));
            }
            if (changed) SendRoster(m);
        }
    }

    /// <summary>Ends rooms nobody plays any more. Returns true when the room was disposed.</summary>
    private bool CheckEmpty(Room<TSim, TInput, TPlayer> m)
    {
        if (m.Ended) return false;
        if (m.Rules.WhenEmpty == EmptyRoomAction.TeamForfeit)
        {
            var seated = m.Teams.Where(t => m.Seats.Any(s => s.Team == t)).ToList();
            if (seated.Count == 1 && m.Get<TeamForfeited>() is null)
            {
                // A whole team is gone: the other one wins (reported once; the game ends the simulation).
                m.Set(new TeamForfeited());
                _game.OnTeamForfeit(m, seated[0]);
                if (m.Ended) return false;
            }
            if (seated.Count > 0 && HasPlayers(m)) return false;
            // No team left, or nobody left to play it: a draw.
            _game.OnForfeitDraw(m);
            Finish(m);
            Dispose(m);
            return true;
        }
        if (HasPlayers(m)) return false;
        Dispose(m);
        return true;
    }

    private sealed class TeamForfeited;

    /// <summary>Someone is connected, about to join, or may still come back.</summary>
    private bool HasPlayers(Room<TSim, TInput, TPlayer> m) =>
        m.ConnectedHumans > 0 || m.Seats.Any(s => s.Pending is not null) || HasRejoinablePlayer(m);

    /// <summary>
    /// A dropped (not deliberately left) player whose grace still runs keeps the room up even with
    /// nobody connected: a network blip on the host side drops every connection at once.
    /// </summary>
    private bool HasRejoinablePlayer(Room<TSim, TInput, TPlayer> m)
    {
        foreach (var p in m.Participants.Values)
            if (!p.Connected && _roomOfPrincipal.GetValueOrDefault(p.PrincipalId) == m)
                return true;
        return false;
    }

    private void Finish(Room<TSim, TInput, TPlayer> m)
    {
        m.Finished = true;
        _game.OnFinished(m);
    }

    /// <summary>Removes a room: its players go back to the menu (or their lobby, through the lobby module).</summary>
    public void Dispose(Room<TSim, TInput, TPlayer> m)
    {
        if (!_rooms.Remove(m)) return;
        RefreshSample();
        foreach (var mod in _modules) mod.OnRoomDisposing(m);
        List<RoomSession<TSim, TInput, TPlayer>>? released = _drainHandled ? new() : null;
        foreach (var p in m.Participants.Values)
        {
            if (_roomOfPrincipal.GetValueOrDefault(p.PrincipalId) == m) UnbindPrincipal(p.PrincipalId);
            if (!_byPrincipal.TryGetValue(p.PrincipalId, out var s) || s.Room != m) continue;
            s.Room = null;
            released?.Add(s);
            if (m.LobbyCode is null) Send(s.ClientId, _game.ReturnedToMenu(m));
        }
        foreach (var mod in _modules) mod.OnRoomDisposed(m);
        if (released is not null)
            foreach (var s in released) TellDraining(s);
    }

    // ------------------------------------------------------------------ reconnect

    private bool TryRejoin(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> m)
    {
        if (m.Ended || !_rooms.Contains(m) || !m.Participants.TryGetValue(s.PrincipalId, out var p) || p.Abandoned) return false;
        if (p.Seat is null)
        {
            // The seat went to a bot: take back a bot (or open) seat of the same team.
            if (!m.Rules.ReclaimTeamSeat || p.DisconnectedAt is not { } at || _now - at > Options.ReconnectGraceSeconds) return false;
            var seat = m.Seats.FirstOrDefault(sl => sl.Team == p.Team && sl.Pending is null && sl.Owner is null);
            if (seat is null) return false;
            m.GiveToHuman(seat, p);
        }
        foreach (var mod in _modules)
            if (!mod.OnRejoining(s, m, p)) return false;
        p.ClientId = s.ClientId;
        p.DisconnectedAt = null;
        p.LeftEarly = false;
        p.Input.Reset();
        s.Room = m;
        BindPrincipal(s.PrincipalId, m);
        Notify(m, RoomNoticeKind.Reconnected, p.Seat!.Id, _game.NameOf(p.Player), except: p);
        SendWelcome(s, p, m);
        SendRoster(m);
        SendSnapshotTo(m, p);
        foreach (var mod in _modules) mod.OnRejoined(s, m);
        return true;
    }

    /// <summary>
    /// The room a fresh connection of this principal would rejoin (see the connect), its
    /// participant and the grace left (null while connected), or null. Lobby rooms are rejoined
    /// through their lobby and are not reported.
    /// </summary>
    public (Room<TSim, TInput, TPlayer> Room, Participant<TSim, TInput, TPlayer> Participant, double? GraceLeft)? ActiveRoomOf(string principalId)
    {
        if (!_roomOfPrincipal.TryGetValue(principalId, out var m) || m.LobbyCode is not null || !CanRejoin(principalId, m)) return null;
        var p = m.Participants[principalId];
        double? graceLeft = p.Connected || p.DisconnectedAt is not { } at ? null : Math.Max(0, Options.ReconnectGraceSeconds - (_now - at));
        return (m, p, graceLeft);
    }

    /// <summary>Mirrors the checks of the rejoin without changing anything.</summary>
    private bool CanRejoin(string principalId, Room<TSim, TInput, TPlayer> m)
    {
        if (m.Ended || !_rooms.Contains(m) || !m.Participants.TryGetValue(principalId, out var p) || p.Abandoned) return false;
        if (p.Seat is not null) return true;
        if (!m.Rules.ReclaimTeamSeat || p.DisconnectedAt is not { } at || _now - at > Options.ReconnectGraceSeconds) return false;
        return m.Seats.Any(sl => sl.Team == p.Team && sl.Pending is null && sl.Owner is null);
    }

    // ------------------------------------------------------------------ sends

    public void Send(string clientId, IPacketBase? packet)
    {
        if (packet is not null) _transport.Send(clientId, packet);
    }

    public void SendWelcome(RoomSession<TSim, TInput, TPlayer> s, Participant<TSim, TInput, TPlayer> p, Room<TSim, TInput, TPlayer> m) =>
        Send(s.ClientId, _game.Welcome(m, p));

    public void SendRoster(Room<TSim, TInput, TPlayer> m)
    {
        var roster = _game.Roster(m);
        if (roster is null) return;
        foreach (var p in m.ActiveHumans) Send(p.ClientId!, roster);
    }

    /// <summary>A notice to the room's active players (optionally all but one).</summary>
    public void Notify(Room<TSim, TInput, TPlayer> m, RoomNoticeKind kind, int seat, string name, Participant<TSim, TInput, TPlayer>? except = null)
    {
        var notice = _game.Notice(m, kind, seat, name);
        if (notice is null) return;
        foreach (var p in m.ActiveHumans)
            if (p != except) Send(p.ClientId!, notice);
    }

    /// <summary>The shared state is captured once per room and round; only the per-receiver fields differ.</summary>
    public void SendSnapshots(Room<TSim, TInput, TPlayer> m)
    {
        object? shared = null;
        foreach (var p in m.ActiveHumans)
        {
            shared ??= _game.CaptureSnapshot(m);
            Send(p.ClientId!, _game.PersonalizeSnapshot(shared, p));
        }
    }

    public void SendSnapshotTo(Room<TSim, TInput, TPlayer> m, Participant<TSim, TInput, TPlayer> p) =>
        Send(p.ClientId!, _game.PersonalizeSnapshot(_game.CaptureSnapshot(m), p));
}
