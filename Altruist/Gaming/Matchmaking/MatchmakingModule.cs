/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.Rooms;

namespace Altruist.Gaming.Matchmaking;

public enum QueueState { Idle, Searching, Found }

/// <summary>A connection's queue status (the game turns it into its packet).</summary>
public sealed record QueueStatusInfo(QueueState State, string Playlist, float Elapsed, int PlayersFound, int PlayersNeeded);

/// <summary>What matchmaking needs from the game.</summary>
public interface IMatchmakingGame<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    /// <summary>The player's rating in a playlist (rated playlists; fresh on every queue entry).</summary>
    int RatingOf(TPlayer player, PlaylistOptions playlist);

    /// <summary>Seconds left on the room's clock (join in progress needs enough of it).</summary>
    double TimeLeftSeconds(Room<TSim, TInput, TPlayer> room);

    IPacketBase QueueStatus(QueueStatusInfo status);
}

/// <summary>Join-in-progress pacing of a room.</summary>
internal sealed class SwapPacing
{
    public double LastSwapAt = double.NegativeInfinity;
}

/// <summary>The mean rating a rated room started with (rated join in progress).</summary>
internal sealed record RoomRating(double Mean);

/// <summary>
/// Queues, groups and join in progress on top of the <see cref="RoomHost{TSim,TInput,TPlayer}"/>:
/// waiting players first fill open (or bot) seats of running rooms that allow it — open seats at
/// once, bot seats one at a time with a notice — and otherwise start new rooms; rated playlists
/// group by rating window (and join rated rooms whose mean rating is inside the player's window).
/// Everything runs on the engine thread.
/// <para>
/// In a fleet (<see cref="RoomHost{TSim,TInput,TPlayer}.UseFleet"/>) each server publishes its
/// queue lengths; the players of a playlist gather on one server — the one with the most of them
/// waiting that still has room (ties: the lowest server id, so every server picks the same one) —
/// and keep their waiting time when they move. A full or draining server sends its queue on.
/// With one server nothing moves.
/// </para>
/// </summary>
public sealed class MatchmakingModule<TSim, TInput, TPlayer> : IRoomHostModule<TSim, TInput, TPlayer>
    where TSim : class, IRoomSimulation<TInput> where TInput : struct
{
    private readonly IMatchmakingGame<TSim, TInput, TPlayer> _game;
    private RoomHost<TSim, TInput, TPlayer> _host = null!;
    private double _nextStatusAt;
    private double _nextPlacementAt;
    private readonly HashSet<string> _dirty = new();

    /// <summary>The <see cref="FleetHandoff.Kind"/> of a queue place handed to another server.</summary>
    public const string HandoffKind = "queue";

    /// <summary>The fleet stat with a playlist's queue length.</summary>
    public static string StatKey(string playlist) => "queue:" + playlist;

    private sealed record Arrival(double At);

    public MatchmakingModule(IMatchmakingGame<TSim, TInput, TPlayer> game, MatchmakingOptions options, IEnumerable<PlaylistOptions> playlists)
    {
        _game = game;
        Queue = new Matchmaker(options, playlists);
    }

    public Matchmaker Queue { get; }
    private MatchmakingOptions O => Queue.Options;

    public void Attach(RoomHost<TSim, TInput, TPlayer> host) => _host = host;

    // ------------------------------------------------------------------ connection commands

    /// <summary>
    /// Puts the connection in a playlist's queue (a re-queue into the same playlist keeps its
    /// place); it leaves its lobby (<see cref="RoomHost{TSim,TInput,TPlayer}.TakeOver"/>). Gates
    /// (verification, bans, ...) are the caller's: check them first. False: the connection plays
    /// in a room or is about to join one (nothing changes), or the server is draining (it is told
    /// it is idle and that the server is going away).
    /// </summary>
    public bool Enqueue(RoomSession<TSim, TInput, TPlayer> s, PlaylistOptions playlist)
    {
        if (s.Room is not null || s.PendingRoom is not null) return false;
        if (_host.IsDraining)
        {
            _host.Send(s.ClientId, _game.QueueStatus(new QueueStatusInfo(QueueState.Idle, playlist.Id, 0, 0, 0)));
            _host.Send(s.ClientId, _host.Game.ServerDraining());
            return false;
        }
        _host.TakeOver(s, this);
        var existing = Queue.Get(s.PrincipalId);
        var queuedAt = existing?.Playlist == playlist.Id ? existing.QueuedAt : _host.Now;
        Queue.Enqueue(new QueueEntry(s.PrincipalId, playlist.Id, _game.RatingOf(s.Player, playlist), queuedAt));
        SendStatus(s, QueueState.Searching);
        _dirty.Add(playlist.Id);
        return true;
    }

    /// <summary>Leaves the queue (and a running join notice); the connection is told it is idle.</summary>
    public void Cancel(RoomSession<TSim, TInput, TPlayer> s)
    {
        var entry = Queue.Get(s.PrincipalId);
        Queue.Remove(s.PrincipalId);
        AbortPendingJoin(s);
        _host.Send(s.ClientId, _game.QueueStatus(new QueueStatusInfo(QueueState.Idle, entry?.Playlist ?? "", 0, 0, 0)));
    }

    /// <summary>Takes the connection out of the queue; true when it was queued.</summary>
    public bool Remove(RoomSession<TSim, TInput, TPlayer> s) => Queue.Remove(s.PrincipalId);

    public void SendStatus(RoomSession<TSim, TInput, TPlayer> s, QueueState state, string? playlistId = null)
    {
        var entry = Queue.Get(s.PrincipalId);
        playlistId ??= entry?.Playlist ?? "";
        var playlist = Queue.Playlist(playlistId);
        var needed = playlist?.PlayersNeeded ?? 0;
        var found = state == QueueState.Found ? needed : Math.Min(needed, playlist is not null ? Queue.Waiting(playlistId).Count : 0);
        _host.Send(s.ClientId, _game.QueueStatus(new QueueStatusInfo(state, playlistId,
            entry is null ? 0 : (float)(_host.Now - entry.QueuedAt), found, needed)));
    }

    /// <summary>Cancels a running join-in-progress notice of this connection.</summary>
    public void AbortPendingJoin(RoomSession<TSim, TInput, TPlayer> s)
    {
        var m = s.PendingRoom;
        if (m is null) return;
        s.PendingRoom = null;
        foreach (var seat in m.Seats)
        {
            if (seat.Pending?.PrincipalId != s.PrincipalId) continue;
            seat.Pending = null;
            _host.Notify(m, RoomNoticeKind.Left, seat.Id, _host.Game.NameOf(s.Player));
        }
    }

    // ------------------------------------------------------------------ host hooks

    public void OnSessionDropping(RoomSession<TSim, TInput, TPlayer> s)
    {
        Queue.Remove(s.PrincipalId);
        AbortPendingJoin(s);
    }

    /// <summary>The connection went into a lobby: it leaves the queue (and a running join notice) and is told it is idle.</summary>
    public void OnTakenOver(RoomSession<TSim, TInput, TPlayer> s)
    {
        if (Queue.Get(s.PrincipalId) is null && s.PendingRoom is null) return;
        Cancel(s);
    }

    public bool IsBusy(RoomSession<TSim, TInput, TPlayer> s) => Queue.Get(s.PrincipalId) is not null;

    /// <summary>Players about to join a room that is going away go back to the queue at their place.</summary>
    public void OnRoomDisposing(Room<TSim, TInput, TPlayer> m)
    {
        foreach (var seat in m.Seats)
        {
            if (seat.Pending is not { } pj) continue;
            seat.Pending = null;
            if (_host.SessionOfPrincipal(pj.PrincipalId) is { } js && js.PendingRoom == m)
            {
                js.PendingRoom = null;
                Requeue(js, m.Playlist!, pj.QueuedAt);
            }
        }
    }

    public void BeforeBotRefills()
    {
        RunPendingSwaps();
        if (!_host.IsDraining) RunMatchmaking();
    }

    /// <summary>
    /// The server is going away: pending joins are called off and the queues empty — into another
    /// server of the fleet when one has room (players keep their waiting time), else everyone is
    /// told they are idle.
    /// </summary>
    public void OnDraining()
    {
        foreach (var m in _host.Rooms.ToArray())
            foreach (var seat in m.Seats.ToArray())
                if (seat.Pending is { } pj && _host.SessionOfPrincipal(pj.PrincipalId) is { } ps && ps.PendingRoom == m)
                {
                    AbortPendingJoin(ps);
                    _host.Send(ps.ClientId, _game.QueueStatus(new QueueStatusInfo(QueueState.Idle, m.Playlist ?? "", 0, 0, 0)));
                }
        foreach (var playlist in Queue.Playlists)
        {
            var target = _host.FleetShared ? QueueTarget(playlist, localCount: 0, selfFits: false) : null;
            foreach (var e in Queue.Waiting(playlist.Id).ToArray())
            {
                if (_host.SessionOfPrincipal(e.PrincipalId) is not { } s)
                {
                    Queue.Remove(e.PrincipalId);
                    continue;
                }
                var idle = _game.QueueStatus(new QueueStatusInfo(QueueState.Idle, playlist.Id, 0, 0, 0));
                if (target is not null && Move(s, e, target, "drain", undo: () => _host.Send(s.ClientId, idle))) continue;
                Queue.Remove(e.PrincipalId);
                _host.Send(s.ClientId, _game.QueueStatus(new QueueStatusInfo(QueueState.Idle, playlist.Id, 0, 0, 0)));
            }
        }
    }

    // ------------------------------------------------------------------ fleet

    /// <summary>A queue place handed over by another server: the player keeps waiting here, with the time already waited.</summary>
    public bool OnHandoff(RoomSession<TSim, TInput, TPlayer> s, FleetHandoff handoff)
    {
        if (handoff.Kind != HandoffKind || _host.IsDraining) return false;
        if (handoff.Get("playlist") is not { } id || Queue.Playlist(id) is not { } playlist) return false;
        var waited = double.TryParse(handoff.Get("waited"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? Math.Max(0, w) : 0;
        Queue.Enqueue(new QueueEntry(s.PrincipalId, playlist.Id, _game.RatingOf(s.Player, playlist), _host.Now - waited));
        s.Set(new Arrival(_host.Now));
        SendStatus(s, QueueState.Searching);
        _dirty.Add(playlist.Id);
        return true;
    }

    /// <summary>Publishes the queue lengths and gathers each playlist's players on one server.</summary>
    private void RunFleet()
    {
        if (_host.Fleet is not { } fleet) return;
        foreach (var p in Queue.Playlists) fleet.SetStat(StatKey(p.Id), Queue.Waiting(p.Id).Count);
        if (!_host.FleetShared || _host.IsDraining || !fleet.IsMultiNode)
        {
            _dirty.Clear();
            return;
        }
        var all = _host.Now >= _nextPlacementAt;
        if (all) _nextPlacementAt = _host.Now + O.StatusIntervalSeconds;
        foreach (var p in Queue.Playlists)
        {
            if (!all && !_dirty.Contains(p.Id)) continue;
            var waiting = Queue.Waiting(p.Id);
            if (waiting.Count == 0) continue;
            var target = QueueTarget(p, waiting.Count, _host.CanOpenRoom(p.Mode, p.Id));
            if (target is null || target == fleet.NodeId) continue;
            foreach (var e in waiting.ToArray())
            {
                if (_host.SessionOfPrincipal(e.PrincipalId) is not { } s || s.PendingRoom is not null) continue;
                if (s.Get<Arrival>() is { } a && _host.Now - a.At < O.FleetMoveCooldownSeconds) continue;
                Move(s, e, target, "queue");
            }
        }
        _dirty.Clear();
    }

    /// <summary>
    /// Where a playlist's players should wait: the server with room for one more of its rooms and
    /// the most of them waiting (ties: the lowest server id); null when no server has room.
    /// </summary>
    private string? QueueTarget(PlaylistOptions p, int localCount, bool selfFits)
    {
        var fleet = _host.Fleet!;
        var cost = _host.Game.LoadOf(p.Mode, p.Id);
        FleetNodeInfo? best = null;
        double bestScore = -1;
        foreach (var n in fleet.Nodes)
        {
            var self = n.NodeId == fleet.NodeId;
            if (self ? !selfFits : !n.Fits(cost)) continue;
            var score = self ? localCount : n.Stat(StatKey(p.Id));
            if (score > bestScore || (score == bestScore && best is not null && string.CompareOrdinal(n.NodeId, best.NodeId) < 0))
            {
                best = n;
                bestScore = score;
            }
        }
        return best?.NodeId;
    }

    private bool Move(RoomSession<TSim, TInput, TPlayer> s, QueueEntry e, string nodeId, string reason, Action? undo = null)
    {
        var handoff = new FleetHandoff(HandoffKind, new Dictionary<string, string>
        {
            ["playlist"] = e.Playlist,
            ["waited"] = (_host.Now - e.QueuedAt).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        }, _host.Fleet!.NodeId);
        Queue.Remove(e.PrincipalId);
        if (_host.Redirect(s, nodeId, reason, handoff, undo: undo ?? (() => Queue.Enqueue(e)))) return true;
        Queue.Enqueue(e);
        return false;
    }

    public void AfterFrame()
    {
        RunFleet();
        if (_host.Now < _nextStatusAt) return;
        _nextStatusAt = _host.Now + O.StatusIntervalSeconds;
        foreach (var p in Queue.Playlists)
            foreach (var e in Queue.Waiting(p.Id))
                if (_host.SessionOfPrincipal(e.PrincipalId) is { } s) SendStatus(s, QueueState.Searching);
    }

    // ------------------------------------------------------------------ matchmaking

    /// <summary>Pairs waiting players with open or bot seats of running rooms, then starts new rooms.</summary>
    private void RunMatchmaking()
    {
        foreach (var playlist in Queue.Playlists)
        {
            if (Queue.Waiting(playlist.Id).Count == 0) continue;

            // Join in progress, oldest first: open seats (a player left) need no pacing; bot swaps one
            // at a time, >= the swap interval apart. Rated: only into rooms inside the player's window.
            foreach (var e in Queue.Waiting(playlist.Id).ToArray())
            {
                if (FindJoinTarget(playlist, e) is not (var m, { } seat))
                {
                    // Unrated targets do not depend on the player: nobody else fits either.
                    if (playlist.Rated) continue;
                    break;
                }
                Queue.Remove(e.PrincipalId);
                AssignJoin(m!, seat, e);
            }

            if (playlist.Rated)
            {
                // A full server keeps the players queued until a room ends (or another server takes them).
                while (_host.CanOpenRoom(playlist.Mode, playlist.Id) && Queue.TryFormRated(playlist.Id, _host.Now) is { } group && StartRoom(playlist, group)) { }
                continue;
            }

            // Players a joinable room takes within a few seconds do not start a new room.
            var reserved = Joinable(playlist).Sum(m => m.Seats.Count(sl => sl.IsBot && sl.Pending is null));
            while (_host.CanOpenRoom(playlist.Mode, playlist.Id) && Queue.TryFormFill(playlist.Id, _host.Now, reserved) is { } group && StartRoom(playlist, group)) { }
        }
    }

    private IEnumerable<Room<TSim, TInput, TPlayer>> Joinable(PlaylistOptions playlist) =>
        _host.Rooms.Where(m => m.Rules.JoinInProgress && m.Playlist == playlist.Id && !m.Ended && _game.TimeLeftSeconds(m) >= O.JoinMinTimeLeftSeconds);

    private static SwapPacing Pacing(Room<TSim, TInput, TPlayer> m)
    {
        var p = m.Get<SwapPacing>();
        if (p is null) m.Set(p = new SwapPacing());
        return p;
    }

    private (Room<TSim, TInput, TPlayer>?, Seat<TSim, TInput, TPlayer>?) FindJoinTarget(PlaylistOptions playlist, QueueEntry entry)
    {
        Room<TSim, TInput, TPlayer>? bestRoom = null;
        Seat<TSim, TInput, TPlayer>? best = null;
        var bestScore = int.MinValue;
        var window = Matchmaker.RatingWindow(_host.Now - entry.QueuedAt, O);
        foreach (var m in Joinable(playlist))
        {
            if (playlist.Rated && (m.Get<RoomRating>() is not { } rating || Math.Abs(entry.Rating - rating.Mean) > window)) continue;
            var swapPending = m.Seats.Any(sl => sl.Pending is not null && sl.IsBot);
            var paced = !swapPending && _host.Now - Pacing(m).LastSwapAt >= O.SwapIntervalSeconds;
            foreach (var seat in m.Seats)
            {
                if (seat.Pending is not null || seat.Owner is not null) continue;
                if (seat.IsBot && !paced) continue;
                // Open seats first, then the team with fewer players (keeps them balanced), then fuller rooms.
                var onTeam = m.Seats.Count(sl => sl.Team == seat.Team && (sl.Owner is not null || sl.Pending is not null));
                var other = m.Seats.Count(sl => sl.Team != seat.Team && (sl.Owner is not null || sl.Pending is not null));
                var score = (seat.IsOpen ? 1000 : 0) + (other - onTeam) * 10 + onTeam + other;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = seat;
                    bestRoom = m;
                }
            }
        }
        return (bestRoom, best);
    }

    private void AssignJoin(Room<TSim, TInput, TPlayer> m, Seat<TSim, TInput, TPlayer> seat, QueueEntry entry)
    {
        if (_host.SessionOfPrincipal(entry.PrincipalId) is not { } s) return;
        var name = _host.Game.NameOf(s.Player);
        // A bot seat changes hands after the notice; an open seat (nobody drives it) is taken at once.
        var open = seat.IsOpen;
        var claim = new PendingSeatClaim(s.PrincipalId, name, open ? _host.Now : _host.Now + O.SwapNoticeSeconds, entry.QueuedAt);
        seat.Pending = claim;
        if (!open) Pacing(m).LastSwapAt = _host.Now;
        s.PendingRoom = m;
        SendStatus(s, QueueState.Found, m.Playlist);
        _host.Notify(m, RoomNoticeKind.Joining, seat.Id, name);
        if (open) CompleteJoin(m, seat, claim);
    }

    private void RunPendingSwaps()
    {
        foreach (var m in _host.Rooms.ToArray())
        {
            // Runs every frame: copy the seats only when a swap is actually due.
            var due = false;
            foreach (var seat in m.Seats)
                if (seat.Pending is { } p && _host.Now >= p.SwapAt) { due = true; break; }
            if (!due) continue;
            foreach (var seat in m.Seats.ToArray())
                if (seat.Pending is { } pj && _host.Now >= pj.SwapAt) CompleteJoin(m, seat, pj);
        }
    }

    private void CompleteJoin(Room<TSim, TInput, TPlayer> m, Seat<TSim, TInput, TPlayer> seat, PendingSeatClaim pj)
    {
        seat.Pending = null;
        if (_host.SessionOfPrincipal(pj.PrincipalId) is not { } s || s.PendingRoom != m) return;
        s.PendingRoom = null;
        if (m.Ended)
        {
            Requeue(s, m.Playlist!, pj.QueuedAt);
            return;
        }
        var wasBot = seat.IsBot;
        var p = m.Join(s.PrincipalId, s.Player, s.ClientId);
        p.Player = s.Player;
        p.DisconnectedAt = null;
        p.LeftEarly = false;
        m.GiveToHuman(seat, p);
        s.Room = m;
        _host.Bind(s.PrincipalId, m);
        if (wasBot) _host.Notify(m, RoomNoticeKind.BotOut, seat.Id, _host.Game.BotNameOf(m, seat), except: p);
        _host.SendWelcome(s, p, m);
        _host.SendRoster(m);
        _host.SendSnapshotTo(m, p);
    }

    private void Requeue(RoomSession<TSim, TInput, TPlayer> s, string playlistId, double queuedAt)
    {
        var playlist = Queue.Playlist(playlistId)!;
        Queue.Enqueue(new QueueEntry(s.PrincipalId, playlistId, _game.RatingOf(s.Player, playlist), queuedAt));
        SendStatus(s, QueueState.Searching);
    }

    /// <summary>Starts a room for the group; false (the group is back in the queue) when the server no longer takes it.</summary>
    private bool StartRoom(PlaylistOptions playlist, List<QueueEntry> group)
    {
        var (team0, team1) = Matchmaker.BalanceTeams(group, byRating: playlist.Rated);
        var m = _host.CreateRoom(_host.Game.CreateSimulation(playlist.Mode, playlist.Id), playlist.Mode, playlist.Id, null, playlist.Rules);
        var seatId = 1;
        var botSeats = new List<Seat<TSim, TInput, TPlayer>>();
        foreach (var (team, members) in new[] { (0, team0), (1, team1) })
        {
            for (var i = 0; i < playlist.TeamSize; i++)
            {
                var seat = m.AddSeat(seatId++, team);
                if (i < members.Count && _host.SessionOfPrincipal(members[i].PrincipalId) is { } s)
                    m.GiveToHuman(seat, m.Join(s.PrincipalId, s.Player, s.ClientId));
                // Unrated rooms fill with bots; rated groups are always complete.
                else botSeats.Add(seat);
            }
        }
        // After every player is in: the game may base the bots on them (skill).
        foreach (var seat in botSeats) m.GiveToBot(seat);
        if (playlist.Rated && group.Count > 0) m.Set(new RoomRating(group.Average(e => e.Rating)));
        if (!_host.TryStart(m))
        {
            // Drained or filled meanwhile (another thread): the players wait on, at their place.
            foreach (var e in group)
                if (_host.SessionOfPrincipal(e.PrincipalId) is not null) Queue.Enqueue(e);
            return false;
        }
        foreach (var p in m.Participants.Values)
        {
            var s = _host.SessionOfPrincipal(p.PrincipalId)!;
            s.Room = m;
            _host.Bind(p.PrincipalId, m);
            SendStatus(s, QueueState.Found, playlist.Id);
            _host.SendWelcome(s, p, m);
        }
        _host.SendRoster(m);
        _host.SendSnapshots(m);
        return true;
    }
}
