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
/// </summary>
public interface IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>Seconds per step.</summary>
    float Dt { get; }

    /// <summary>The simulation reached its end (match over); the host finishes the room.</summary>
    bool Ended { get; }

    void AddSeat(int seat, int team);
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
/// <param name="Reason"><c>rejoin</c> (its room runs there), <c>queue</c>, <c>lobby</c> or <c>drain</c>.</param>
public sealed record RoomRedirect(string NodeId, string NodeParam, string? PublicAddress, string Reason);

/// <summary>A server-side driver of a seat (a bot), asked once per step.</summary>
public interface ISeatController<TInput> where TInput : struct
{
    TInput Think();
}

/// <summary>A join-in-progress claim: <see cref="PrincipalId"/> takes the seat at <see cref="SwapAt"/>.</summary>
public sealed record PendingSeatClaim(string PrincipalId, string Name, double SwapAt, double QueuedAt);

public enum RoomNoticeKind { Joining, Left, BotIn, BotOut, Reconnected }

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
    RemoveSeat,
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
    public DisconnectAction OnDisconnect { get; init; } = DisconnectAction.Hold;
    public GraceExpiredAction OnGraceExpired { get; init; } = GraceExpiredAction.RemoveSeat;
    public VoluntaryLeaveAction OnVoluntaryLeave { get; init; } = VoluntaryLeaveAction.RemoveSeat;
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
    public required string ClientId { get; init; }
    public required string PrincipalId { get; init; }
    public required TPlayer Player { get; set; }
    /// <summary>The room this connection plays in.</summary>
    public Room<TSim, TInput, TPlayer>? Room { get; set; }
    /// <summary>The room this connection is about to join (join-in-progress notice running).</summary>
    public Room<TSim, TInput, TPlayer>? PendingRoom { get; set; }

    private readonly Dictionary<Type, object> _features = new();

    /// <summary>Per-connection state of a module or the game (lobby membership, chat rate, ...).</summary>
    public T? Get<T>() where T : class => _features.TryGetValue(typeof(T), out var v) ? (T)v : null;

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
    public required string PrincipalId { get; init; }
    public required TPlayer Player { get; set; }
    public int Team { get; set; }
    /// <summary>Connection driving this participant; null while disconnected.</summary>
    public string? ClientId { get; set; }
    /// <summary>Seat owned; null after it went to a bot or was removed.</summary>
    public Seat<TSim, TInput, TPlayer>? Seat { get; set; }
    /// <summary>The last seat this participant drove.</summary>
    public int LastSeat { get; set; }
    public double? DisconnectedAt { get; set; }
    /// <summary>Left and did not come back.</summary>
    public bool LeftEarly { get; set; }
    /// <summary>Abandon already reported to the game.</summary>
    public bool Abandoned { get; set; }
    public required InputBuffer<TInput> Input { get; init; }
    /// <summary>The game's per-participant state (stat tallies, ...).</summary>
    public object? GameData { get; set; }

    public bool Connected => ClientId is not null;
}

/// <summary>One seat of a room: a human owner, a bot, or nobody (open, waiting for a claim or a bot).</summary>
public sealed class Seat<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    public required int Id { get; init; }
    public required int Team { get; init; }
    public Participant<TSim, TInput, TPlayer>? Owner { get; set; }
    public ISeatController<TInput>? Bot { get; set; }
    /// <summary>When the seat lost its controller (null while controlled).</summary>
    public double? OpenSince { get; set; }
    /// <summary>When a bot takes over the open seat if nobody claims it.</summary>
    public double? BotAt { get; set; }
    public PendingSeatClaim? Pending { get; set; }
    /// <summary>The game's per-seat state (a bot name, ...).</summary>
    public object? GameData { get; set; }

    public bool IsBot => Bot is not null;
    public bool IsOpen => Owner is null && Bot is null;
}
