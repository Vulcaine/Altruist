/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// The game's authoritative simulation of one room. Single-threaded: the host calls it one
/// fixed step at a time, on the engine thread or (with a multi-worker <see cref="IStepScheduler"/>)
/// on one worker per step, never two threads at once; it may only touch its own state while stepping. Seats are identified by an int the game chooses
/// (a car id, a slot number, ...), teams by an int.
/// <para>
/// Implement it for anything a <see cref="RoomHost{TSim,TInput,TPlayer}"/> runs: the host collects one
/// input per seat (player, bot or <see cref="IRoomGame{TSim,TInput,TPlayer}.NeutralInput"/>) and calls
/// <see cref="Step"/>. Determinism is up to the game (iterate seats in a fixed order, not dictionary order,
/// if bit-identical replays matter).
/// </para>
/// </summary>
public interface IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>Seconds per step.</summary>
    float Dt { get; }

    /// <summary>The simulation reached its end (match over); the host finishes the room.</summary>
    bool Ended { get; }

    /// <summary>A seat joins the simulation (spawn its body); called by <see cref="Room{TSim,TInput,TPlayer}.AddSeat"/>.</summary>
    void AddSeat(int seat, int team);
    /// <summary>A seat leaves the simulation (despawn its body); it gets no more inputs.</summary>
    void RemoveSeat(int seat);

    /// <summary>A server controller (bot) drives the seat from now on, or no longer does.</summary>
    void SetBotControlled(int seat, bool bot);

    /// <summary>Exactly one step; <paramref name="inputs"/> has an entry for every seat.</summary>
    void Step(IReadOnlyDictionary<int, TInput> inputs);
}

/// <summary>
/// Sends a connection to another server of the fleet: the client reconnects through the same
/// public address with <c>?{NodeParam}={NodeId}</c> (or straight to <see cref="PublicAddress"/>
/// when the server has one of its own) and says "rejoin"; the work it had here (a queue place, a
/// lobby) is waiting for it there.
/// </summary>
/// <param name="NodeId">The target server's node id.</param>
/// <param name="NodeParam">The query parameter the load balancer routes on (the fleet's <c>NodeParam</c>).</param>
/// <param name="PublicAddress">The target server's own public address, or null (reconnect through the shared address).</param>
/// <param name="Reason"><c>rejoin</c> (its room runs there), <c>queue</c>, <c>lobby</c> or <c>drain</c>.</param>
public sealed record RoomRedirect(string NodeId, string NodeParam, string? PublicAddress, string Reason);

/// <summary>A server-side driver of a seat (a bot), asked once per step.</summary>
public interface ISeatController<TInput> where TInput : struct
{
    /// <summary>The seat's input for the coming step. May run on a step worker: touch only this room's state.</summary>
    TInput Think();
}

/// <summary>A join-in-progress claim: <see cref="PrincipalId"/> takes the seat at <see cref="SwapAt"/>.</summary>
/// <param name="PrincipalId">The queued player taking the seat.</param>
/// <param name="Name">Its display name (for the <see cref="RoomNoticeKind"/> <c>Joining</c> notice).</param>
/// <param name="SwapAt">Host time (seconds, <see cref="RoomHost{TSim,TInput,TPlayer}.Now"/>) the seat changes hands.</param>
/// <param name="QueuedAt">Host time the player entered the queue (keeps its place if the claim falls through).</param>
public sealed record PendingSeatClaim(string PrincipalId, string Name, double SwapAt, double QueuedAt);

/// <summary>
/// Kinds of room notices (<see cref="IRoomGame{TSim,TInput,TPlayer}.Notice"/>): a queued player
/// <c>Joining</c> a seat in progress, a player <c>Left</c>, a bot took a seat (<c>BotIn</c>) or
/// handed it to a player (<c>BotOut</c>), a player <c>Reconnected</c>.
/// </summary>
public enum RoomNoticeKind
{
    /// <summary>A queued player is joining a seat of a match in progress.</summary>
    Joining,
    /// <summary>A player left.</summary>
    Left,
    /// <summary>A bot took a seat.</summary>
    BotIn,
    /// <summary>A bot handed its seat to a player.</summary>
    BotOut,
    /// <summary>A player reconnected.</summary>
    Reconnected,
}

/// <summary>What happens when a seated player's connection drops.</summary>
public enum DisconnectAction
{
    /// <summary>The seat waits for the player until the grace runs out.</summary>
    Hold,
    /// <summary>A bot takes the seat after <see cref="RoomHostOptions.BotRefillDelaySeconds"/>; the player may reclaim a team seat within the grace.</summary>
    BotAfterDelay,
}

/// <summary>What happens to a disconnected player when the grace runs out.</summary>
public enum GraceExpiredAction
{
    /// <summary>
    /// The seat stays and a bot drives it (with <see cref="DisconnectAction.Hold"/> the held seat
    /// opens for a bot now); only the right to rejoin ends.
    /// </summary>
    ForfeitRejoin,
    /// <summary>The seat leaves the room.</summary>
    RemoveSeat,
    /// <summary>The seat leaves and the game is told (penalties, records).</summary>
    Abandon,
}

/// <summary>What happens when a player leaves on purpose.</summary>
public enum VoluntaryLeaveAction
{
    /// <summary>The seat opens; a bot takes it after the refill delay unless a queued player claims it.</summary>
    OpenForBot,
    /// <summary>The seat leaves the room.</summary>
    RemoveSeat,
    /// <summary>The seat leaves and the game is told (<see cref="IRoomGame{TSim,TInput,TPlayer}.OnAbandoned"/>: penalties, records).</summary>
    Abandon,
}

/// <summary>When a room with nobody in it ends.</summary>
public enum EmptyRoomAction
{
    /// <summary>Disposed once no player is connected, joining or still allowed to rejoin.</summary>
    Dispose,
    /// <summary>
    /// A team without seats forfeits (reported once); no team left, or no player connected,
    /// joining or still allowed to rejoin: a draw (finished and disposed).
    /// </summary>
    TeamForfeit,
}

/// <summary>Per-room lifecycle rules (set by whoever creates the room: matchmaking, a lobby).</summary>
public sealed record RoomRules
{
    /// <summary>What happens to a seat when its player's connection drops (default: hold it for the grace).</summary>
    public DisconnectAction OnDisconnect { get; init; } = DisconnectAction.Hold;
    /// <summary>What happens when a disconnected player's grace (<see cref="RoomHostOptions.ReconnectGraceSeconds"/>) runs out (default: remove the seat).</summary>
    public GraceExpiredAction OnGraceExpired { get; init; } = GraceExpiredAction.RemoveSeat;
    /// <summary>What happens when a player leaves on purpose (<see cref="RoomHost{TSim,TInput,TPlayer}.Return"/>; default: remove the seat). Leaving on purpose always ends the right to rejoin.</summary>
    public VoluntaryLeaveAction OnVoluntaryLeave { get; init; } = VoluntaryLeaveAction.RemoveSeat;
    /// <summary>When a room nobody plays any more ends (default: disposed once nobody is connected, joining or allowed to rejoin).</summary>
    public EmptyRoomAction WhenEmpty { get; init; } = EmptyRoomAction.Dispose;
    /// <summary>A returning player may take a bot or open seat of their team when their own seat went to a bot.</summary>
    public bool ReclaimTeamSeat { get; init; }
    /// <summary>Queued players may join while it runs (matchmaking backfill; rated rooms take players inside their rating window).</summary>
    public bool JoinInProgress { get; init; }
    /// <summary>
    /// Seats opened by the rules (<see cref="DisconnectAction.BotAfterDelay"/>, <see cref="VoluntaryLeaveAction.OpenForBot"/>,
    /// <see cref="GraceExpiredAction.ForfeitRejoin"/>) get a bot at <see cref="Seat{TSim,TInput,TPlayer}.BotAt"/>
    /// (default). False: they stay open for queued players only (<see cref="JoinInProgress"/>).
    /// </summary>
    public bool BotRefill { get; init; } = true;
}

/// <summary>Timings of the host (passed by the game; not read from configuration).</summary>
public sealed record RoomHostOptions
{
    /// <summary>Snapshots go out every N host steps (global phase: every room in the same step).</summary>
    public int SnapshotEveryNSteps { get; init; } = 2;
    /// <summary>Seconds after a seat opens (or its player drops) before a bot takes it.</summary>
    public double BotRefillDelaySeconds { get; init; } = 2;
    /// <summary>Seconds a disconnected player may come back into the same room.</summary>
    public double ReconnectGraceSeconds { get; init; } = 30;
    /// <summary>Seconds an ended room stays before its players are released.</summary>
    public double ReturnAfterEndSeconds { get; init; } = 6;
    /// <summary>Inputs buffered beyond this are merged or dropped, oldest first (when <see cref="Input"/> is not set).</summary>
    public int MaxQueuedInputs { get; init; } = 16;
    /// <summary>Input buffering (jitter target, catching up); null: the defaults with <see cref="MaxQueuedInputs"/>.</summary>
    public InputBufferOptions? Input { get; init; }
    /// <summary>
    /// The simulation keeps stepping after it ended, until the room is released (an end screen
    /// with moving players; default). False: an ended room stops stepping, snapshots go on.
    /// </summary>
    public bool StepAfterEnd { get; init; } = true;
}

/// <summary>One connection of a principal (at most one per principal: the newest wins).</summary>
public sealed class RoomSession<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>The connection id.</summary>
    public required string ClientId { get; init; }
    /// <summary>The player identity (account id) behind the connection.</summary>
    public required string PrincipalId { get; init; }
    /// <summary>The game's player data; changed with <see cref="RoomHost{TSim,TInput,TPlayer}.UpdatePlayer"/>.</summary>
    public required TPlayer Player { get; set; }
    /// <summary>The room this connection plays in.</summary>
    public Room<TSim, TInput, TPlayer>? Room { get; set; }
    /// <summary>The room this connection is about to join (join-in-progress notice running).</summary>
    public Room<TSim, TInput, TPlayer>? PendingRoom { get; set; }

    private readonly Dictionary<Type, object> _features = new();

    /// <summary>Per-connection state of a module or the game (lobby membership, chat rate, ...).</summary>
    public T? Get<T>() where T : class => _features.TryGetValue(typeof(T), out var v) ? (T)v : null;

    /// <summary>Stores (or, with null, removes) the per-connection state of type <typeparamref name="T"/>.</summary>
    public void Set<T>(T? value) where T : class
    {
        if (value is null) _features.Remove(typeof(T));
        else _features[typeof(T)] = value;
    }
}

/// <summary>A player taking part in a room (connected, or disconnected within the grace).</summary>
public sealed class Participant<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>The player identity (account id).</summary>
    public required string PrincipalId { get; init; }
    /// <summary>The game's player data as of joining (or the latest profile update).</summary>
    public required TPlayer Player { get; set; }
    /// <summary>The team of the seat it last took.</summary>
    public int Team { get; set; }
    /// <summary>Connection driving this participant; null while disconnected.</summary>
    public string? ClientId { get; set; }
    /// <summary>Seat owned; null after it went to a bot or was removed.</summary>
    public Seat<TSim, TInput, TPlayer>? Seat { get; set; }
    /// <summary>The last seat this participant drove.</summary>
    public int LastSeat { get; set; }
    /// <summary>Host time (<see cref="RoomHost{TSim,TInput,TPlayer}.Now"/>, seconds) the connection dropped or left; null while connected.</summary>
    public double? DisconnectedAt { get; set; }
    /// <summary>Left and did not come back.</summary>
    public bool LeftEarly { get; set; }
    /// <summary>Abandon already reported to the game.</summary>
    public bool Abandoned { get; set; }
    /// <summary>The participant's sequenced inputs (reset whenever it is seated, rejoins or leaves).</summary>
    public required InputBuffer<TInput> Input { get; init; }
    /// <summary>The game's per-participant state (stat tallies, ...).</summary>
    public object? GameData { get; set; }

    /// <summary>A connection drives the participant.</summary>
    public bool Connected => ClientId is not null;
}

/// <summary>One seat of a room: a human owner, a bot, or nobody (open, waiting for a claim or a bot).</summary>
public sealed class Seat<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>The game's seat id (unique in the room).</summary>
    public required int Id { get; init; }
    /// <summary>The seat's team.</summary>
    public required int Team { get; init; }
    /// <summary>The player owning the seat (connected or within the grace), or null.</summary>
    public Participant<TSim, TInput, TPlayer>? Owner { get; set; }
    /// <summary>The bot driving the seat, or null.</summary>
    public ISeatController<TInput>? Bot { get; set; }
    /// <summary>When the seat lost its controller (null while controlled).</summary>
    public double? OpenSince { get; set; }
    /// <summary>When a bot takes over the open seat if nobody claims it.</summary>
    public double? BotAt { get; set; }
    /// <summary>A queued player about to take this seat (join in progress), or null.</summary>
    public PendingSeatClaim? Pending { get; set; }
    /// <summary>The game's per-seat state (a bot name, ...).</summary>
    public object? GameData { get; set; }

    /// <summary>A bot drives the seat.</summary>
    public bool IsBot => Bot is not null;
    /// <summary>Nobody (player or bot) drives the seat.</summary>
    public bool IsOpen => Owner is null && Bot is null;
}
