/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// What the game plugs into the room host: its seat bookkeeping, bots, results and wire format.
/// The host owns connections, seats, inputs, timing, reconnects and disposal; it never looks
/// inside the packets.
/// </summary>
public interface IRoomGame<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>The input of an idle seat (also the starting "last input" of a player).</summary>
    TInput NeutralInput { get; }

    /// <summary>Display name of a player (notices).</summary>
    string NameOf(TPlayer player);

    /// <summary>Display name of the bot driving (or about to drive) a seat.</summary>
    string BotNameOf(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat);

    /// <summary>The simulation of a new room.</summary>
    TSim CreateSimulation(string mode, string? playlist);

    /// <summary>
    /// The load a room of this mode puts on the server, in the units of
    /// <c>altruist:server:capacity:max-load</c> (default 1: max-load is a room count). A host
    /// bound to an <see cref="IServerNode"/> opens a room only when the load still fits.
    /// </summary>
    double LoadOf(string mode, string? playlist) => 1;

    /// <summary>A room was created around a simulation (before its seats are added).</summary>
    void OnRoomCreated(Room<TSim, TInput, TPlayer> room) { }

    /// <summary>The room is about to run, every seat added (reset the simulation: kickoff, clock).</summary>
    void OnStarting(Room<TSim, TInput, TPlayer> room) { }

    // ---------------------------------------------------------------- seats and steps

    void OnSeatAdded(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat) { }

    /// <summary>A new bot for the seat.</summary>
    ISeatController<TInput> CreateBot(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat);

    /// <summary>A player took the seat; <paramref name="newDriver"/>: someone else (or nobody) had it before.</summary>
    void OnHumanSeated(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat, Participant<TSim, TInput, TPlayer> p, bool newDriver) { }

    void OnBotSeated(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat) { }

    /// <summary>The owner gives the seat up (move per-seat stats to the player, ...).</summary>
    void OnReleased(Room<TSim, TInput, TPlayer> room, Seat<TSim, TInput, TPlayer> seat, Participant<TSim, TInput, TPlayer> p) { }

    /// <summary>
    /// Right before each simulation step (inputs collected). With a multi-worker
    /// <see cref="IStepScheduler"/> rooms step in parallel: this hook, the bots'
    /// <see cref="ISeatController{TInput}.Think"/> and the simulation's step may only touch their own room.
    /// </summary>
    void BeforeStep(Room<TSim, TInput, TPlayer> room) { }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>The room ended (simulation over, or a forfeit draw): compute and persist results.</summary>
    void OnFinished(Room<TSim, TInput, TPlayer> room) { }

    /// <summary>A player abandoned (<see cref="GraceExpiredAction.Abandon"/>, <see cref="VoluntaryLeaveAction.Abandon"/>).</summary>
    void OnAbandoned(Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p) { }

    /// <summary><see cref="EmptyRoomAction.TeamForfeit"/>: one team is left (<paramref name="winnerTeam"/>); end the simulation. Called once per room.</summary>
    void OnTeamForfeit(Room<TSim, TInput, TPlayer> room, int winnerTeam) { }

    /// <summary>
    /// <see cref="EmptyRoomAction.TeamForfeit"/>: no team is left, or no player is connected, joining
    /// or allowed to rejoin: the room ends in a draw (finished and disposed right after).
    /// </summary>
    void OnForfeitDraw(Room<TSim, TInput, TPlayer> room) { }

    /// <summary>A newer connection of the same principal replaces this one (tell it before it closes).</summary>
    void OnSessionReplaced(RoomSession<TSim, TInput, TPlayer> old) { }

    /// <summary>
    /// The server's drain timed out with the room still running: it is closed now, without a
    /// regular end (record it as cancelled). Its players are released right after.
    /// </summary>
    void OnForcedClose(Room<TSim, TInput, TPlayer> room) { }

    // ---------------------------------------------------------------- wire format

    IPacketBase? Welcome(Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p);

    IPacketBase? Roster(Room<TSim, TInput, TPlayer> room);

    IPacketBase? Notice(Room<TSim, TInput, TPlayer> room, RoomNoticeKind kind, int seat, string name);

    /// <summary>Room state shared by every receiver of one snapshot round (a fresh copy).</summary>
    object CaptureSnapshot(Room<TSim, TInput, TPlayer> room);

    /// <summary>One receiver's snapshot: the shared state plus their ack and input depth.</summary>
    IPacketBase PersonalizeSnapshot(object shared, Participant<TSim, TInput, TPlayer> p);

    /// <summary>Sent to a player released from a room that has no lobby to return to.</summary>
    IPacketBase? ReturnedToMenu(Room<TSim, TInput, TPlayer> room);

    /// <summary>Answer to a rejoin request of a connection that has nothing to rejoin.</summary>
    IPacketBase? NothingToRejoin();

    /// <summary>
    /// The server is going away: sent when the drain begins to every connection without a room
    /// (menu, queue, lobby), to players released from a room during the drain and to new
    /// connections that have nothing to rejoin. The client should take new work elsewhere.
    /// </summary>
    IPacketBase? ServerDraining() => null;

    /// <summary>
    /// The packet that sends a client to another server of the fleet (see <see cref="RoomRedirect"/>).
    /// Null (the default) keeps every player on the server it connected to: the fleet then only
    /// balances new connections through the load balancer.
    /// </summary>
    IPacketBase? Redirect(RoomRedirect redirect) => null;
}

/// <summary>Where the host's packets go (the outbound queues in production, a recorder in tests).</summary>
public interface IRoomTransport
{
    void Send(string clientId, IPacketBase packet);

    /// <summary>Closes the connection after what was queued before.</summary>
    void Disconnect(string clientId);
}

/// <summary>
/// A feature on top of the host (matchmaking, lobbies). Hooks run in registration order at
/// fixed points of the host's own flow.
/// </summary>
public interface IRoomHostModule<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    void Attach(RoomHost<TSim, TInput, TPlayer> host) { }

    /// <summary>A connection is closing (or replaced), before its room seat is let go.</summary>
    void OnSessionDropping(RoomSession<TSim, TInput, TPlayer> s) { }

    /// <summary>The player of a connection changed (profile update).</summary>
    void OnPlayerUpdated(RoomSession<TSim, TInput, TPlayer> s) { }

    /// <summary>The connection asked to leave its room; true when the module handled it.</summary>
    bool TryReturn(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p) => false;

    /// <summary>A returning connection is being seated again; false refuses the rejoin.</summary>
    bool OnRejoining(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p) => true;

    /// <summary>A returning connection was seated again and has its welcome and snapshot.</summary>
    void OnRejoined(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room) { }

    /// <summary>The room was removed from the host, before its participants are released.</summary>
    void OnRoomDisposing(Room<TSim, TInput, TPlayer> room) { }

    /// <summary>The room's participants were released.</summary>
    void OnRoomDisposed(Room<TSim, TInput, TPlayer> room) { }

    /// <summary>Every frame, after the reconnect timers and before bots refill open seats.</summary>
    void BeforeBotRefills() { }

    /// <summary>Every frame, last.</summary>
    void AfterFrame() { }

    /// <summary>
    /// Another module took the connection for its work (<see cref="RoomHost{TSim,TInput,TPlayer}.TakeOver"/>:
    /// it queued, it joined a lobby): let go of it.
    /// </summary>
    void OnTakenOver(RoomSession<TSim, TInput, TPlayer> s) { }

    /// <summary>The connection waits in this module (a queue, a lobby): it has something going on.</summary>
    bool IsBusy(RoomSession<TSim, TInput, TPlayer> s) => false;

    /// <summary>The server began draining (once, on the engine thread): stop starting new work.</summary>
    void OnDraining() { }

    /// <summary>
    /// A connection arrived from another server with work handed over (<see cref="FleetHandoff.Kind"/>
    /// <c>queue</c>, <c>lobby</c>, or the game's own); true when this module took it.
    /// </summary>
    bool OnHandoff(RoomSession<TSim, TInput, TPlayer> s, FleetHandoff handoff) => false;
}
