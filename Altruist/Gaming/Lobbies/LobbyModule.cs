/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.Rooms;

namespace Altruist.Gaming.Lobbies;

/// <summary>One member of a <see cref="Lobby{TSim,TInput,TPlayer}"/>.</summary>
/// <typeparam name="TPlayer">The game's player data.</typeparam>
public sealed class LobbyMember<TPlayer>
{
    /// <summary>The member's current connection (updated when it rejoins).</summary>
    public required string ClientId { get; set; }
    /// <summary>The member's player identity.</summary>
    public required string PrincipalId { get; init; }
    /// <summary>Stable id inside the lobby (also the member's seat id in its rooms).</summary>
    public required int MemberId { get; init; }
    /// <summary>The member's player data (kept up to date on profile changes).</summary>
    public required TPlayer Player { get; set; }
    /// <summary>
    /// False while the member's connection is gone but its seat in the lobby's room may still be
    /// rejoined: it keeps its place (and the host role) until the grace or the room ends.
    /// </summary>
    public bool Connected { get; set; } = true;
}

/// <summary>
/// A private group joined by invite code. The host (first member; the next one when the host
/// leaves) picks a mode and starts rooms; after a room everyone comes back here.
/// </summary>
public sealed class Lobby<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    private int _nextMemberId = 1;

    /// <summary>An empty lobby; <see cref="LobbyModule{TSim,TInput,TPlayer}"/> creates them (use <see cref="LobbyModule{TSim,TInput,TPlayer}.Join"/>).</summary>
    /// <param name="code">The invite code.</param>
    public Lobby(string code) => Code = code;

    /// <summary>The invite code (upper case).</summary>
    public string Code { get; }
    /// <summary>Members in join order (also the order the host role passes on).</summary>
    public List<LobbyMember<TPlayer>> Members { get; } = new();
    /// <summary>The member that may start rooms ("" in an empty lobby).</summary>
    public string HostPrincipalId { get; private set; } = "";
    /// <summary>The room the lobby is playing, or null.</summary>
    public Room<TSim, TInput, TPlayer>? Room { get; internal set; }

    /// <summary>Adds a member; <paramref name="memberId"/> restores the id of a returning member.</summary>
    public LobbyMember<TPlayer> Add(string clientId, string principalId, TPlayer player, int? memberId = null)
    {
        var m = new LobbyMember<TPlayer> { ClientId = clientId, PrincipalId = principalId, MemberId = memberId ?? _nextMemberId++, Player = player };
        Members.Add(m);
        if (string.IsNullOrEmpty(HostPrincipalId)) HostPrincipalId = principalId;
        return m;
    }

    /// <summary>Removes a member; the host role passes to the next connected member (else the next one).</summary>
    public void Remove(string principalId)
    {
        Members.RemoveAll(m => m.PrincipalId == principalId);
        // The host role goes to the next member still connected (else the next one).
        if (HostPrincipalId == principalId) HostPrincipalId = (Members.FirstOrDefault(m => m.Connected) ?? Members.FirstOrDefault())?.PrincipalId ?? "";
    }

    /// <summary>The member with this principal, or null.</summary>
    public LobbyMember<TPlayer>? Get(string principalId) => Members.FirstOrDefault(m => m.PrincipalId == principalId);

    /// <summary>The principal holds the host role.</summary>
    public bool IsHost(string principalId) => HostPrincipalId == principalId;

    /// <summary>Hands the host role to a member (a lobby moved to another server keeps its host).</summary>
    public void MakeHost(string principalId)
    {
        if (Get(principalId) is not null) HostPrincipalId = principalId;
    }
}

/// <summary>Settings of <see cref="LobbyModule{TSim,TInput,TPlayer}"/> (passed by the game; not read from configuration).</summary>
public sealed record LobbyOptions
{
    /// <summary>Members a lobby takes; joining a full lobby is refused with <see cref="LobbyRejectReason.Full"/>.</summary>
    public int MaxMembers { get; init; } = 4;
    /// <summary>Characters in a generated invite code.</summary>
    public int CodeLength { get; init; } = 5;
    /// <summary>Unambiguous code characters.</summary>
    public string CodeAlphabet { get; init; } = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    /// <summary>Seat ids of bots added at start (members use their member ids).</summary>
    public int BotSeatIdBase { get; init; } = 50;
    /// <summary>The playlist name lobby rooms carry.</summary>
    public string Playlist { get; init; } = "private";
    /// <summary>Lifecycle rules of lobby rooms (default: a seat waits for its player, leaving removes it).</summary>
    public RoomRules Rules { get; init; } = new();
}

/// <summary>
/// Why a lobby request was refused: <see cref="NotFound"/> = no lobby has the code;
/// <see cref="Full"/> = the lobby has <see cref="LobbyOptions.MaxMembers"/> members;
/// <see cref="Unavailable"/> = the server cannot take it (it is draining, or full when starting a
/// room); <see cref="TooManyPlayers"/> = the lobby has more members than the mode has seats.
/// </summary>
public enum LobbyRejectReason
{
    /// <summary>No lobby has the code.</summary>
    NotFound,
    /// <summary>The lobby already has <see cref="LobbyOptions.MaxMembers"/> members.</summary>
    Full,
    /// <summary>The server cannot take it (draining, or full when starting a room).</summary>
    Unavailable,
    /// <summary>The lobby has more members than the mode has seats.</summary>
    TooManyPlayers,
}

/// <summary>
/// What lobbies need from the game; implement it (usually on the same class as
/// <see cref="IRoomGame{TSim,TInput,TPlayer}"/>) when the host uses <see cref="LobbyModule{TSim,TInput,TPlayer}"/>.
/// Called on the engine thread.
/// </summary>
public interface ILobbyGame<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>
    /// Seats per team of the mode (bots fill up to it; more members than both teams' seats cannot
    /// start it), 0 for a mode without fixed teams (every member plays, no bots), or null when the
    /// mode is not allowed.
    /// </summary>
    int? TeamSizeOf(string mode);

    /// <summary>The lobby's state for one member.</summary>
    IPacketBase LobbyState(Lobby<TSim, TInput, TPlayer> lobby, LobbyMember<TPlayer> to);

    /// <summary>The packet that tells a connection its lobby request was refused.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="code">The code it asked for ("" for a new lobby).</param>
    IPacketBase Rejected(LobbyRejectReason reason, string code);
}

/// <summary>The lobby a connection is in.</summary>
internal sealed class LobbySeat<TSim, TInput, TPlayer>(Lobby<TSim, TInput, TPlayer> lobby)
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    public readonly Lobby<TSim, TInput, TPlayer> Lobby = lobby;
}

/// <summary>
/// Private lobbies on top of the <see cref="RoomHost{TSim,TInput,TPlayer}"/>: create or join by
/// code, host migration, start a room with the members (bots fill the teams), return after the
/// room, rejoin a lobby room after a reconnect. A member whose connection drops during the
/// lobby's room keeps its place and role for the room's reconnect grace.
/// <para>
/// In a fleet a lobby lives on the server it was created on and its code is claimed there: a
/// player joining the code on another server is sent to it. A draining server moves its lobbies
/// (members, code and host) to another server once they are not playing a room.
/// </para>
/// <para>
/// When to use: private play among invited players (friends, custom games, training) where a leader
/// picks the mode and starts, and the group stays together between rooms. For public queues that
/// group strangers by playlist and rating, with backfill and join in progress, use
/// <c>MatchmakingModule</c> (Altruist.Gaming.Matchmaking) instead; both can be added to one host and
/// joining a lobby takes the connection out of its queue (<see cref="RoomHost{TSim,TInput,TPlayer}.TakeOver"/>).
/// Lobby rooms carry <see cref="Room{TSim,TInput,TPlayer}.LobbyCode"/>; their players return to the
/// lobby (not the menu) when the room ends, and the host member's <see cref="RoomHost{TSim,TInput,TPlayer}.Return"/>
/// ends the room for everyone.
/// </para>
/// <example>
/// <code>
/// var lobbies = new LobbyModule&lt;ArenaSim, PadInput, Profile&gt;(game,
///     new LobbyOptions { MaxMembers = 6, Playlist = "private", Rules = new RoomRules { BotRefill = false } });
/// host.Use(lobbies);
/// // engine thread, on client commands:
/// lobbies.Join(session, "");          // create a lobby (the code comes back in LobbyState)
/// lobbies.Join(session, "K7QXD");     // join by code (any server of the fleet)
/// lobbies.Start(session, "2v2");      // host only; ILobbyGame.TeamSizeOf("2v2") must not be null
/// </code>
/// </example>
/// </summary>
public sealed class LobbyModule<TSim, TInput, TPlayer> : IRoomHostModule<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    private readonly ILobbyGame<TSim, TInput, TPlayer> _game;
    private readonly Dictionary<string, Lobby<TSim, TInput, TPlayer>> _lobbies = new();
    private RoomHost<TSim, TInput, TPlayer> _host = null!;

    /// <summary>The fleet unit kind lobby codes are claimed under, and the <see cref="FleetHandoff.Kind"/> of a lobby move.</summary>
    public const string LobbyUnit = "lobby";

    /// <summary>Creates the module; add it to a host with <see cref="RoomHost{TSim,TInput,TPlayer}.Use"/>.</summary>
    /// <param name="game">The game's lobby hooks (usually the same object as the host's <see cref="IRoomGame{TSim,TInput,TPlayer}"/>).</param>
    /// <param name="options">Lobby size, codes, playlist name and room rules.</param>
    public LobbyModule(ILobbyGame<TSim, TInput, TPlayer> game, LobbyOptions options)
    {
        _game = game;
        Options = options;
    }

    /// <summary>The options it was created with.</summary>
    public LobbyOptions Options { get; }

    /// <inheritdoc/>
    public void Attach(RoomHost<TSim, TInput, TPlayer> host) => _host = host;

    /// <summary>Open lobbies on this server.</summary>
    public IReadOnlyCollection<Lobby<TSim, TInput, TPlayer>> Lobbies => _lobbies.Values;

    /// <summary>The lobby the connection is in, or null.</summary>
    public Lobby<TSim, TInput, TPlayer>? LobbyOf(RoomSession<TSim, TInput, TPlayer> s) => s.Get<LobbySeat<TSim, TInput, TPlayer>>()?.Lobby;

    private static void SetLobby(RoomSession<TSim, TInput, TPlayer> s, Lobby<TSim, TInput, TPlayer>? lobby) =>
        s.Set(lobby is null ? null : new LobbySeat<TSim, TInput, TPlayer>(lobby));

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Joins the lobby with this code, or creates one for an empty code. The connection leaves its
    /// queue (<see cref="RoomHost{TSim,TInput,TPlayer}.TakeOver"/>) and its previous lobby; a
    /// connection playing in a room is ignored.
    /// </summary>
    public void Join(RoomSession<TSim, TInput, TPlayer> s, string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (s.Room is not null) return;
        if (LobbyOf(s) is { } current)
        {
            if (current.Code == code)
            {
                SendLobby(current);
                return;
            }
            Leave(s, sendUpdate: true);
        }
        _host.TakeOver(s, this);
        if (_host.IsDraining)
        {
            // Another server takes it: the code's server, or any server for a new lobby.
            if (_host.FleetShared && code.Length > 0) FindElsewhere(s, code);
            else if (!(_host.FleetShared && _host.OtherServerFor(1) is { } other
                && _host.Redirect(s, other.NodeId, "drain", new FleetHandoff(LobbyUnit, new Dictionary<string, string> { ["code"] = "" }, _host.Fleet!.NodeId))))
                _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.Unavailable, code));
            return;
        }
        if (!_lobbies.TryGetValue(code, out var lobby))
        {
            if (code.Length > 0)
            {
                if (_host.FleetShared) FindElsewhere(s, code);
                else _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.NotFound, code));
                return;
            }
            lobby = Open(NewCode());
        }
        Enter(s, lobby);
    }

    private Lobby<TSim, TInput, TPlayer> Open(string code)
    {
        var lobby = new Lobby<TSim, TInput, TPlayer>(code);
        _lobbies[code] = lobby;
        _host.Fleet?.Claim(LobbyUnit, code);
        return lobby;
    }

    private void Close(Lobby<TSim, TInput, TPlayer> lobby)
    {
        if (_lobbies.Remove(lobby.Code)) _host.Fleet?.Release(LobbyUnit, lobby.Code);
    }

    /// <summary>The code is not here: the server that runs it gets the player, or the code does not exist.</summary>
    private void FindElsewhere(RoomSession<TSim, TInput, TPlayer> s, string code)
    {
        var fleet = _host.Fleet!;
        _host.RunAsync(() => fleet.LocateAsync(LobbyUnit, code), owner =>
        {
            if (_host.SessionOf(s.ClientId) != s || LobbyOf(s) is not null) return;
            if (_lobbies.TryGetValue(code, out var here) && !_host.IsDraining)
            {
                Enter(s, here);
                return;
            }
            var handoff = new FleetHandoff(LobbyUnit, new Dictionary<string, string> { ["code"] = code }, fleet.NodeId);
            if (owner is null || !_host.Redirect(s, owner, "lobby", handoff))
                _host.Send(s.ClientId, _game.Rejected(owner == fleet.NodeId && _host.IsDraining ? LobbyRejectReason.Unavailable : LobbyRejectReason.NotFound, code));
        }, _ => _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.NotFound, code)));
    }

    private void Enter(RoomSession<TSim, TInput, TPlayer> s, Lobby<TSim, TInput, TPlayer> lobby)
    {
        if (lobby.Members.Count >= Options.MaxMembers)
        {
            _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.Full, lobby.Code));
            return;
        }
        lobby.Add(s.ClientId, s.PrincipalId, s.Player);
        SetLobby(s, lobby);
        SendLobby(lobby);
    }

    /// <summary>The connection leaves its lobby (an empty lobby closes and its room is disposed).</summary>
    /// <param name="s">The connection.</param>
    /// <param name="sendUpdate">Send the new lobby state to the remaining members.</param>
    public void Leave(RoomSession<TSim, TInput, TPlayer> s, bool sendUpdate)
    {
        var lobby = LobbyOf(s);
        if (lobby is null) return;
        SetLobby(s, null);
        lobby.Remove(s.PrincipalId);
        if (CloseIfEmpty(lobby)) return;
        if (sendUpdate) SendLobby(lobby);
    }

    /// <summary>A lobby without members closes (and its room goes).</summary>
    private bool CloseIfEmpty(Lobby<TSim, TInput, TPlayer> lobby)
    {
        if (lobby.Members.Count > 0) return false;
        Close(lobby);
        if (lobby.Room is { } m) _host.Dispose(m);
        return true;
    }

    /// <summary>Members whose connection is gone and who can no longer rejoin the lobby's room leave it.</summary>
    private void PruneDisconnected(Lobby<TSim, TInput, TPlayer> lobby)
    {
        var gone = false;
        foreach (var mb in lobby.Members.ToArray())
        {
            if (mb.Connected || (lobby.Room is { } room && _host.BoundRoom(mb.PrincipalId) == room)) continue;
            lobby.Remove(mb.PrincipalId);
            gone = true;
        }
        if (gone && !CloseIfEmpty(lobby)) SendLobby(lobby);
    }

    /// <summary>
    /// The host starts a room in a mode: members alternate teams, bots fill each team to the mode's
    /// size. Refused (<see cref="LobbyRejectReason.TooManyPlayers"/>) when the members do not fit the
    /// mode's seats, and (<see cref="LobbyRejectReason.Unavailable"/>) when the server cannot take the room.
    /// </summary>
    public void Start(RoomSession<TSim, TInput, TPlayer> s, string mode)
    {
        var lobby = LobbyOf(s);
        if (lobby is null || !lobby.IsHost(s.PrincipalId) || _game.TeamSizeOf(mode) is not { } teamSize || lobby.Room is not null) return;
        if (teamSize > 0 && lobby.Members.Count > teamSize * 2)
        {
            _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.TooManyPlayers, lobby.Code));
            return;
        }
        if (!_host.CanOpenRoom(mode, Options.Playlist))
        {
            _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.Unavailable, lobby.Code));
            return;
        }
        var m = _host.CreateRoom(_host.Game.CreateSimulation(mode, Options.Playlist), mode, Options.Playlist, lobby.Code, Options.Rules);
        m.Set(lobby);
        var team0 = 0;
        var team1 = 0;
        foreach (var member in lobby.Members)
        {
            var team = team0 <= team1 ? 0 : 1;
            if (team == 0) team0++;
            else team1++;
            var seat = m.AddSeat(member.MemberId, team);
            var p = m.Join(member.PrincipalId, member.Player, member.ClientId);
            m.GiveToHuman(seat, p);
        }
        var botId = Options.BotSeatIdBase;
        while (team0 < teamSize)
        {
            m.GiveToBot(m.AddSeat(botId++, 0));
            team0++;
        }
        while (team1 < teamSize)
        {
            m.GiveToBot(m.AddSeat(botId++, 1));
            team1++;
        }
        if (!_host.TryStart(m))
        {
            _host.Send(s.ClientId, _game.Rejected(LobbyRejectReason.Unavailable, lobby.Code));
            return;
        }
        lobby.Room = m;
        foreach (var p in m.Participants.Values)
        {
            var ms = _host.SessionOfPrincipal(p.PrincipalId)!;
            ms.Room = m;
            _host.Bind(p.PrincipalId, m);
            _host.SendWelcome(ms, p, m);
        }
        _host.SendRoster(m);
        SendLobby(lobby);
        _host.SendSnapshots(m);
    }

    /// <summary>Sends every connected member its <see cref="ILobbyGame{TSim,TInput,TPlayer}.LobbyState"/>.</summary>
    public void SendLobby(Lobby<TSim, TInput, TPlayer> lobby)
    {
        foreach (var mb in lobby.Members)
            if (mb.Connected) _host.Send(mb.ClientId, _game.LobbyState(lobby, mb));
    }

    private string NewCode()
    {
        var chars = Options.CodeAlphabet;
        while (true)
        {
            var code = new string(Enumerable.Range(0, Options.CodeLength)
                .Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            if (!_lobbies.ContainsKey(code)) return code;
        }
    }

    // ------------------------------------------------------------------ host hooks

    /// <summary>The connection is closing: it leaves its lobby, unless it drops out of the lobby's running room (then its place and role wait for the rejoin).</summary>
    public void OnSessionDropping(RoomSession<TSim, TInput, TPlayer> s)
    {
        if (LobbyOf(s) is not { } lobby) return;
        if (lobby.Room is { } room && s.Room == room && !room.Ended && lobby.Get(s.PrincipalId) is { } member)
        {
            // Dropped out of the lobby's room: the place (and host role) waits for the rejoin.
            SetLobby(s, null);
            member.Connected = false;
            SendLobby(lobby);
            return;
        }
        Leave(s, sendUpdate: true);
    }

    /// <summary>The connection queued: it leaves its lobby.</summary>
    public void OnTakenOver(RoomSession<TSim, TInput, TPlayer> s)
    {
        if (LobbyOf(s) is not null) Leave(s, sendUpdate: true);
    }

    /// <summary>Members whose rejoin grace ran out leave their lobby.</summary>
    public void AfterFrame()
    {
        foreach (var lobby in _lobbies.Values.ToArray())
            if (lobby.Room is not null && lobby.Members.Any(mb => !mb.Connected)) PruneDisconnected(lobby);
    }

    /// <summary>The member's player data changed: the lobby is updated and resent.</summary>
    public void OnPlayerUpdated(RoomSession<TSim, TInput, TPlayer> s)
    {
        if (LobbyOf(s) is { } lobby && lobby.Get(s.PrincipalId) is { } member)
        {
            member.Player = s.Player;
            SendLobby(lobby);
        }
    }

    /// <summary>The connection is in a lobby.</summary>
    public bool IsBusy(RoomSession<TSim, TInput, TPlayer> s) => LobbyOf(s) is not null;

    /// <summary>The host ends the room for everyone; other members just step out of it.</summary>
    public bool TryReturn(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p)
    {
        if (room.Get<Lobby<TSim, TInput, TPlayer>>() is not { } lobby) return false;
        if (lobby.IsHost(s.PrincipalId))
        {
            _host.Dispose(room);
            return true;
        }
        s.Room = null;
        _host.ParticipantLeft(room, p, voluntary: true);
        if (_host.Contains(room)) SendLobby(lobby);
        return true;
    }

    /// <summary>A member rejoining a lobby room gets its lobby place back (re-added when it was pruned; refused when the lobby is full meanwhile).</summary>
    public bool OnRejoining(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room, Participant<TSim, TInput, TPlayer> p)
    {
        if (room.Get<Lobby<TSim, TInput, TPlayer>>() is not { } lobby) return true;
        if (lobby.Get(s.PrincipalId) is null)
        {
            if (lobby.Members.Count >= Options.MaxMembers) return false;
            lobby.Add(s.ClientId, s.PrincipalId, s.Player, p.Seat!.Id);
        }
        var mb = lobby.Get(s.PrincipalId)!;
        mb.ClientId = s.ClientId;
        mb.Connected = true;
        SetLobby(s, lobby);
        return true;
    }

    /// <summary>Resends the lobby state after a member rejoined its room.</summary>
    public void OnRejoined(RoomSession<TSim, TInput, TPlayer> s, Room<TSim, TInput, TPlayer> room)
    {
        if (room.Get<Lobby<TSim, TInput, TPlayer>>() is { } lobby) SendLobby(lobby);
    }

    /// <summary>The lobby's room ended: members still away leave, the rest are back in the lobby (on a draining server the lobby then moves to another server).</summary>
    public void OnRoomDisposed(Room<TSim, TInput, TPlayer> room)
    {
        if (room.Get<Lobby<TSim, TInput, TPlayer>>() is not { } lobby || lobby.Room != room) return;
        lobby.Room = null;
        if (!_lobbies.ContainsKey(lobby.Code)) return;
        // Nobody can rejoin the room any more: members still away leave (the host role moves on).
        if (lobby.Members.Any(mb => !mb.Connected))
        {
            foreach (var mb in lobby.Members.Where(mb => !mb.Connected).ToArray()) lobby.Remove(mb.PrincipalId);
            if (CloseIfEmpty(lobby)) return;
        }
        SendLobby(lobby);
        if (_host.IsDraining) MoveAway(lobby);
    }

    // ------------------------------------------------------------------ fleet

    /// <summary>The server is going away: lobbies not playing a room move to another server now, the others after their room.</summary>
    public void OnDraining()
    {
        foreach (var lobby in _lobbies.Values.ToArray())
            if (lobby.Room is null) MoveAway(lobby);
    }

    /// <summary>Every member goes to the same other server, carrying the code and the host role.</summary>
    private void MoveAway(Lobby<TSim, TInput, TPlayer> lobby)
    {
        if (!_host.FleetShared || _host.OtherServerFor(1) is not { } target) return;
        foreach (var member in lobby.Members.ToArray())
        {
            if (_host.SessionOfPrincipal(member.PrincipalId) is not { } s) continue;
            var handoff = new FleetHandoff(LobbyUnit, new Dictionary<string, string>
            {
                ["code"] = lobby.Code,
                ["host"] = lobby.IsHost(member.PrincipalId) ? "1" : "0",
            }, _host.Fleet!.NodeId);
            _host.Redirect(s, target.NodeId, "drain", handoff);
        }
    }

    /// <summary>
    /// A player sent here for a lobby: joins it, or re-opens it under the same code when the lobby
    /// is moving here from a draining server (the old host gets the host role back).
    /// </summary>
    public bool OnHandoff(RoomSession<TSim, TInput, TPlayer> s, FleetHandoff handoff)
    {
        if (handoff.Kind != LobbyUnit || _host.IsDraining) return false;
        var code = handoff.Get("code") ?? "";
        // An empty code: a new lobby the player asked for on a draining server.
        var lobby = code.Length == 0 ? Open(NewCode()) : _lobbies.GetValueOrDefault(code) ?? Open(code);
        Enter(s, lobby);
        if (handoff.Get("host") == "1" && lobby.Get(s.PrincipalId) is not null && !lobby.IsHost(s.PrincipalId))
        {
            lobby.MakeHost(s.PrincipalId);
            SendLobby(lobby);
        }
        return true;
    }
}
