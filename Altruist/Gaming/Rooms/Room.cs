/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// One running room: the game's simulation plus its seats and participants. Seats keep their
/// simulated body across controller changes (a bot taking over keeps position and velocity).
/// Only touched from the engine thread.
/// <para>
/// Created with <see cref="RoomHost{TSim,TInput,TPlayer}.CreateRoom"/> (matchmaking and lobbies do
/// that for you), seated with <see cref="AddSeat"/> + <see cref="Join"/> + <see cref="GiveToHuman"/>
/// or <see cref="GiveToBot"/>, then started with <see cref="RoomHost{TSim,TInput,TPlayer}.TryStart"/>.
/// </para>
/// </summary>
public sealed class Room<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    private readonly IRoomGame<TSim, TInput, TPlayer> _game;
    private readonly RoomHostOptions _options;
    private readonly Dictionary<int, TInput> _stepInputs = new();
    private readonly Dictionary<Type, object> _features = new();

    internal Room(IRoomGame<TSim, TInput, TPlayer> game, RoomHostOptions options, TSim sim, string mode, string? playlist,
        string? lobbyCode, RoomRules rules, DateTime startedAtUtc)
    {
        _game = game;
        _options = options;
        Sim = sim;
        Mode = mode;
        Playlist = playlist;
        LobbyCode = lobbyCode;
        Rules = rules;
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>Unique room id (a GUID without dashes), assigned on creation.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");
    /// <summary>The game mode the room was created for (passed to <see cref="IRoomGame{TSim,TInput,TPlayer}.CreateSimulation"/>).</summary>
    public string Mode { get; }
    /// <summary>The playlist (queue) the room came from, or null (lobby or tool rooms may have none).</summary>
    public string? Playlist { get; }
    /// <summary>The lobby that started the room (its players go back there), or null.</summary>
    public string? LobbyCode { get; }
    /// <summary>Lifecycle rules of this room (disconnects, grace, leaving, empty room, backfill).</summary>
    public RoomRules Rules { get; }
    /// <summary>Wall-clock creation time (UTC, from the host's clock).</summary>
    public DateTime StartedAtUtc { get; }
    /// <summary>The load the room puts on the server (<see cref="IRoomGame{TSim,TInput,TPlayer}.LoadOf"/>).</summary>
    public double Load { get; internal set; } = 1;
    /// <summary>The game's simulation of this room.</summary>
    public TSim Sim { get; }
    /// <summary>The room's seats in the order they were added. Change them through <see cref="AddSeat"/> and <see cref="RemoveSeat"/>, not directly.</summary>
    public List<Seat<TSim, TInput, TPlayer>> Seats { get; } = new();
    /// <summary>Every player that took part, by principal id (including disconnected and departed ones, for results).</summary>
    public Dictionary<string, Participant<TSim, TInput, TPlayer>> Participants { get; } = new();
    /// <summary>Every team that had a seat, in order of appearance (team forfeits).</summary>
    public List<int> Teams { get; } = new();
    /// <summary>Seconds since the simulation ended.</summary>
    public float EndedSeconds { get; private set; }
    /// <summary>The end was handled (results handed to the game).</summary>
    public bool Finished { get; internal set; }

    /// <summary>The room is over: the end was handled or the simulation reports it ended.</summary>
    public bool Ended => Finished || Sim.Ended;

    /// <summary>Connected participants that currently own a seat.</summary>
    public IEnumerable<Participant<TSim, TInput, TPlayer>> ActiveHumans => Participants.Values.Where(p => p.Connected && p.Seat is not null);

    /// <summary>Participants with a live connection (seated or not).</summary>
    public int ConnectedHumans => Participants.Values.Count(p => p.Connected);

    /// <summary>The seat with the game's seat id, or null.</summary>
    public Seat<TSim, TInput, TPlayer>? SeatOf(int id) => Seats.FirstOrDefault(s => s.Id == id);

    /// <summary>Per-room state of a module (join-in-progress pacing, ...).</summary>
    public T? Get<T>() where T : class => _features.TryGetValue(typeof(T), out var v) ? (T)v : null;

    /// <summary>Stores (or, with null, removes) the per-room state of type <typeparamref name="T"/> (see <see cref="Get{T}"/>).</summary>
    public void Set<T>(T? value) where T : class
    {
        if (value is null) _features.Remove(typeof(T));
        else _features[typeof(T)] = value;
    }

    // ------------------------------------------------------------------ seats

    /// <summary>
    /// Adds a seat (open: no owner, no bot) to the room and the simulation and calls
    /// <see cref="IRoomGame{TSim,TInput,TPlayer}.OnSeatAdded"/>. Then give it to a player
    /// (<see cref="GiveToHuman"/>) or a bot (<see cref="GiveToBot"/>).
    /// </summary>
    /// <param name="id">The game's seat id (unique in the room).</param>
    /// <param name="team">The seat's team.</param>
    /// <returns>The new seat.</returns>
    public Seat<TSim, TInput, TPlayer> AddSeat(int id, int team)
    {
        var seat = new Seat<TSim, TInput, TPlayer> { Id = id, Team = team };
        Seats.Add(seat);
        if (!Teams.Contains(team)) Teams.Add(team);
        Sim.AddSeat(id, team);
        _game.OnSeatAdded(this, seat);
        return seat;
    }

    /// <summary>
    /// The participant for a principal, created on first use (with a fresh input buffer); an existing
    /// one keeps its player data and only gets the new <paramref name="clientId"/>. Does not seat it.
    /// </summary>
    /// <param name="principalId">The player identity.</param>
    /// <param name="player">The game's player data (used only when the participant is created).</param>
    /// <param name="clientId">The connection driving it, or null (not connected yet).</param>
    public Participant<TSim, TInput, TPlayer> Join(string principalId, TPlayer player, string? clientId)
    {
        if (!Participants.TryGetValue(principalId, out var p))
        {
            p = new Participant<TSim, TInput, TPlayer>
            {
                PrincipalId = principalId,
                Player = player,
                Input = new InputBuffer<TInput>(_game.NeutralInput, _game.InputModel,
                    _options.Input ?? new InputBufferOptions { MaxQueued = _options.MaxQueuedInputs }),
            };
            Participants[principalId] = p;
        }
        p.ClientId = clientId;
        return p;
    }

    /// <summary>Hands the seat to a player; a bot is dropped, a new driver starts fresh.</summary>
    public void GiveToHuman(Seat<TSim, TInput, TPlayer> seat, Participant<TSim, TInput, TPlayer> p)
    {
        if (seat.Owner is not null && seat.Owner != p) Release(seat);
        var newDriver = seat.Owner != p;
        seat.Bot = null;
        seat.Owner = p;
        seat.OpenSince = null;
        seat.BotAt = null;
        p.Seat = seat;
        p.LastSeat = seat.Id;
        p.Team = seat.Team;
        p.Input.Reset();
        _game.OnHumanSeated(this, seat, p, newDriver);
        Sim.SetBotControlled(seat.Id, false);
    }

    /// <summary>Hands the seat to a fresh bot.</summary>
    public void GiveToBot(Seat<TSim, TInput, TPlayer> seat)
    {
        if (seat.Owner is not null) Release(seat);
        seat.Bot = _game.CreateBot(this, seat);
        seat.OpenSince = null;
        seat.BotAt = null;
        _game.OnBotSeated(this, seat);
        Sim.SetBotControlled(seat.Id, true);
    }

    /// <summary>The owner gives the seat up (the game moves its per-seat stats to the player).</summary>
    public void Release(Seat<TSim, TInput, TPlayer> seat)
    {
        var p = seat.Owner;
        if (p is null) return;
        _game.OnReleased(this, seat, p);
        p.Seat = null;
        seat.Owner = null;
    }

    /// <summary>The seat leaves the room and the simulation.</summary>
    public void RemoveSeat(Seat<TSim, TInput, TPlayer> seat)
    {
        Release(seat);
        Seats.Remove(seat);
        Sim.RemoveSeat(seat.Id);
    }

    // ------------------------------------------------------------------ step

    /// <summary>
    /// One fixed step: one input per connected owner (repeated when starved), bots think, others
    /// idle. After the simulation ended it only steps with <see cref="RoomHostOptions.StepAfterEnd"/>.
    /// The host steps its rooms; tools may step a room they created outside it.
    /// </summary>
    public void Step()
    {
        if (Sim.Ended && !_options.StepAfterEnd)
        {
            EndedSeconds += Sim.Dt;
            return;
        }
        var inputs = _stepInputs;
        inputs.Clear();
        foreach (var s in Seats)
        {
            if (s.Owner is { Connected: true } p) inputs[s.Id] = p.Input.Consume();
            else if (s.Bot is not null) inputs[s.Id] = s.Bot.Think();
            else inputs[s.Id] = _game.NeutralInput;
        }
        _game.BeforeStep(this);
        Sim.Step(inputs);
        if (Sim.Ended) EndedSeconds += Sim.Dt;
    }
}
