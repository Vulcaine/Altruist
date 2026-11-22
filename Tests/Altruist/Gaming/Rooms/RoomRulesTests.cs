/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Lobbies;
using Altruist.Gaming.Matchmaking;
using Altruist.Gaming.Rooms;

namespace Tests.Gaming.Rooms;

/// <summary>
/// Every room rule does what it promises: grace expiry with each disconnect action, bots for
/// opened seats, team forfeits, rated join in progress, open seats taken at once, lobby rooms
/// surviving a disconnect, team sizes, the connection's state checks, and ended rooms.
/// </summary>
public class RoomRulesTests
{
    private static PlaylistOptions Playlist(string id, RoomRules rules, bool rated = false, int teamSize = 1) => new()
    {
        Id = id,
        Mode = "duel",
        TeamSize = teamSize,
        Rated = rated,
        Rules = rules,
    };

    private sealed class Harness
    {
        public readonly LineGame Game = new();
        public readonly RoomHost<LineSim, Walk, Player> Host;
        public readonly MatchmakingModule<LineSim, Walk, Player> Mm;
        public readonly LobbyModule<LineSim, Walk, Player> Lobbies;
        private readonly WorldCoordinator _world;

        public Harness(IEnumerable<PlaylistOptions> playlists, RoomHostOptions? options = null, LobbyOptions? lobby = null)
        {
            Host = new RoomHost<LineSim, Walk, Player>(Game, Game, options ?? new RoomHostOptions { ReconnectGraceSeconds = 5 }, 60);
            Mm = new MatchmakingModule<LineSim, Walk, Player>(Game, new MatchmakingOptions(), playlists);
            Lobbies = new LobbyModule<LineSim, Walk, Player>(Game, lobby ?? new LobbyOptions());
            Host.Use(Mm).Use(Lobbies);
            _world = new WorldCoordinator(new IWorldStepper[] { Host });
        }

        public RoomSession<LineSim, Walk, Player> Connect(string who, int rating = 1000, string? client = null) =>
            Host.Connect(client ?? "c-" + who, who, new Player(who, who.ToUpperInvariant(), rating))!;

        public bool Queue(string who, PlaylistOptions playlist, string? client = null) => Mm.Enqueue(Host.SessionOf(client ?? "c-" + who)!, playlist);

        public void Run(double seconds)
        {
            for (var i = 0; i < (int)Math.Round(seconds * 60); i++) _world.Step(1f / 60f);
        }

        /// <summary>Two players matched into one room of the playlist.</summary>
        public Room<LineSim, Walk, Player> Pair(PlaylistOptions playlist, int ratingA = 1000, int ratingB = 1000)
        {
            Connect("a", ratingA);
            Connect("b", ratingB);
            Queue("a", playlist);
            Queue("b", playlist);
            Run(0.05);
            return Assert.Single(Host.Rooms);
        }
    }

    private static string SeatsOf(Room<LineSim, Walk, Player> room) =>
        string.Join(",", room.Seats.Select(s => s.Owner?.PrincipalId ?? (s.IsBot ? "bot" : "open")));

    // ------------------------------------------------------------------ grace expiry

    [Fact]
    public void Hold_then_ForfeitRejoin_hands_the_held_seat_to_a_bot()
    {
        var p = Playlist("p", new RoomRules { OnDisconnect = DisconnectAction.Hold, OnGraceExpired = GraceExpiredAction.ForfeitRejoin });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        var seat = room.Participants["b"].Seat!;
        h.Host.Disconnect("c-b");
        h.Run(4);
        Assert.Same(room.Participants["b"], seat.Owner); // held through the grace
        h.Run(1.1);
        Assert.True(seat.IsBot);
        Assert.Contains($"notice:BotIn:{seat.Id}:bot{seat.Id}", h.Game.To("c-a"));
        Assert.True(room.Sim.Seats[seat.Id].Bot);
        Assert.Null(h.Host.ActiveRoomOf("b"));
        // Too late: the player comes back to nothing.
        h.Connect("b", client: "c-b2");
        Assert.Null(h.Host.SessionOf("c-b2")!.Room);
        Assert.True(seat.IsBot);
    }

    [Fact]
    public void Hold_then_ForfeitRejoin_without_bot_refill_opens_the_seat_for_a_queued_player()
    {
        var p = Playlist("p", new RoomRules
        {
            OnDisconnect = DisconnectAction.Hold,
            OnGraceExpired = GraceExpiredAction.ForfeitRejoin,
            JoinInProgress = true,
            BotRefill = false,
        });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        var seat = room.Participants["b"].Seat!;
        h.Host.Disconnect("c-b");
        h.Run(5.1);
        Assert.True(seat.IsOpen);
        h.Run(3);
        Assert.True(seat.IsOpen); // no bot comes
        h.Connect("c");
        h.Queue("c", p);
        h.Run(1 / 60.0);
        Assert.Equal("c", seat.Owner?.PrincipalId);
    }

    [Theory]
    [InlineData(GraceExpiredAction.RemoveSeat)]
    [InlineData(GraceExpiredAction.Abandon)]
    public void BotAfterDelay_applies_the_grace_rule_to_the_seat_its_bot_took(GraceExpiredAction expired)
    {
        var p = Playlist("p", new RoomRules { OnDisconnect = DisconnectAction.BotAfterDelay, OnGraceExpired = expired });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        var seat = room.Participants["b"].Seat!;
        h.Host.Disconnect("c-b");
        h.Run(2.1);
        Assert.True(seat.IsBot); // the bot drives it within the grace (bots come by default)
        h.Run(3);
        Assert.DoesNotContain(seat, room.Seats);
        Assert.False(room.Sim.Seats.ContainsKey(seat.Id));
        Assert.Equal(expired == GraceExpiredAction.Abandon ? new[] { "b" } : Array.Empty<string>(), h.Game.Abandons);
        Assert.Null(h.Host.ActiveRoomOf("b"));
        Assert.Contains("roster:a", h.Game.To("c-a"));
    }

    [Fact]
    public void BotAfterDelay_with_ForfeitRejoin_keeps_the_bot_after_the_grace()
    {
        var p = Playlist("p", new RoomRules { OnDisconnect = DisconnectAction.BotAfterDelay, OnGraceExpired = GraceExpiredAction.ForfeitRejoin });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        h.Host.Disconnect("c-b");
        h.Run(6);
        Assert.Equal("a,bot", SeatsOf(room));
        Assert.Null(h.Host.ActiveRoomOf("b"));
        Assert.Empty(h.Game.Abandons);
    }

    [Fact]
    public void A_seat_reclaimed_by_a_teammate_is_not_removed_with_the_player_who_left_it()
    {
        var p = Playlist("p", new RoomRules
        {
            OnDisconnect = DisconnectAction.BotAfterDelay,
            OnGraceExpired = GraceExpiredAction.RemoveSeat,
            ReclaimTeamSeat = true,
        }, teamSize: 2);
        var h = new Harness(new[] { p });
        foreach (var who in new[] { "a", "b", "c", "d" })
        {
            h.Connect(who);
            h.Queue(who, p);
        }
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        var team = room.Participants["a"].Team;
        var mate = room.Participants.Values.First(x => x.PrincipalId != "a" && x.Team == team);
        var aSeat = room.Participants["a"].Seat!;
        // Both drop; the teammate comes back and the free team seat it takes may be a's.
        h.Host.Disconnect("c-a");
        h.Host.Disconnect("c-" + mate.PrincipalId);
        h.Run(2.1);
        h.Run(1);
        h.Connect(mate.PrincipalId, client: "c-mate2");
        var taken = room.Participants[mate.PrincipalId].Seat!;
        h.Run(2.5); // a's grace ends
        Assert.Contains(taken, room.Seats);
        Assert.Equal(mate.PrincipalId, taken.Owner?.PrincipalId);
        // a's seat leaves with a, unless the teammate took it (then the teammate's old seat keeps its bot).
        if (taken == aSeat) Assert.Equal(4, room.Seats.Count);
        else
        {
            Assert.Equal(3, room.Seats.Count);
            Assert.DoesNotContain(aSeat, room.Seats);
        }
    }

    // ------------------------------------------------------------------ bots for opened seats

    [Fact]
    public void Seats_opened_by_the_rules_get_bots_by_default()
    {
        Assert.True(new RoomRules().BotRefill);
        var p = Playlist("p", new RoomRules { OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot });
        var h = new Harness(new[] { p }, new RoomHostOptions { BotRefillDelaySeconds = 1 });
        var room = h.Pair(p);
        h.Host.Return("c-b");
        Assert.Equal("a,open", SeatsOf(room));
        h.Run(1.1);
        Assert.Equal("a,bot", SeatsOf(room));
    }

    // ------------------------------------------------------------------ team forfeits

    [Fact]
    public void A_team_forfeit_is_reported_once_even_when_the_game_lets_the_room_run()
    {
        var p = Playlist("p", new RoomRules
        {
            OnVoluntaryLeave = VoluntaryLeaveAction.Abandon,
            WhenEmpty = EmptyRoomAction.TeamForfeit,
        }, teamSize: 2);
        var h = new Harness(new[] { p });
        h.Game.EndOnForfeit = false;
        foreach (var who in new[] { "a", "b", "c", "d" })
        {
            h.Connect(who);
            h.Queue(who, p);
        }
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        var team = room.Participants["a"].Team;
        var mates = room.Participants.Values.Where(x => x.Team == team).Select(x => x.PrincipalId).ToList();
        var others = room.Participants.Values.Where(x => x.Team != team).Select(x => x.PrincipalId).ToList();
        foreach (var who in mates) h.Host.Return("c-" + who);
        Assert.Equal(1, h.Game.TeamForfeits);
        // One of the winners leaves too: still one team left, no second report.
        h.Host.Return("c-" + others[0]);
        Assert.Equal(1, h.Game.TeamForfeits);
        Assert.Single(h.Host.Rooms);
        Assert.Empty(h.Game.Draws);
    }

    [Fact]
    public void A_team_forfeit_room_with_nobody_left_to_play_ends_in_a_draw()
    {
        // Seats stay (bots take them), so no team ever runs out of seats.
        var p = Playlist("p", new RoomRules
        {
            OnDisconnect = DisconnectAction.Hold,
            OnGraceExpired = GraceExpiredAction.ForfeitRejoin,
            WhenEmpty = EmptyRoomAction.TeamForfeit,
        });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        h.Host.Disconnect("c-a");
        h.Host.Disconnect("c-b");
        h.Run(3);
        Assert.Single(h.Host.Rooms); // both may still come back
        h.Run(2.1);
        Assert.Empty(h.Host.Rooms);
        Assert.Equal(new[] { room.Id }, h.Game.Draws);
        Assert.Equal(new[] { room.Id }, h.Game.Finished);
        Assert.Equal(0, h.Game.TeamForfeits);
    }

    // ------------------------------------------------------------------ join in progress

    [Fact]
    public void An_open_seat_is_taken_at_once_and_a_bot_seat_after_the_notice()
    {
        var p = Playlist("p", new RoomRules
        {
            OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot,
            JoinInProgress = true,
        });
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        h.Host.Return("c-b");
        h.Connect("c");
        h.Queue("c", p);
        h.Run(1 / 60.0);
        Assert.Equal("a,c", SeatsOf(room));
        Assert.Contains(h.Game.To("c-a"), x => x.StartsWith("notice:Joining") && x.EndsWith(":C"));
        Assert.Contains("welcome:2", h.Game.To("c-c"));

        // A bot seat changes hands only after the notice.
        h.Host.Return("c-c");
        h.Run(2.1);
        Assert.Equal("a,bot", SeatsOf(room));
        h.Connect("d");
        h.Queue("d", p);
        h.Run(1 / 60.0);
        Assert.Equal("a,bot", SeatsOf(room));
        h.Run(2.1);
        Assert.Equal("a,d", SeatsOf(room));
    }

    [Fact]
    public void Rated_rooms_take_queued_players_inside_their_rating_window()
    {
        var p = Playlist("r", new RoomRules
        {
            OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot,
            JoinInProgress = true,
            BotRefill = false,
        }, rated: true);
        var h = new Harness(new[] { p });
        var room = h.Pair(p, 1000, 1050);
        h.Host.Return("c-b");
        Assert.Equal("a,open", SeatsOf(room));
        // The room's mean is 1025: 1400 is outside the starting window (100), 1100 inside.
        h.Connect("far", rating: 1400);
        h.Queue("far", p);
        h.Run(1 / 60.0);
        Assert.Equal("a,open", SeatsOf(room));
        h.Connect("near", rating: 1100);
        h.Queue("near", p);
        h.Run(1 / 60.0);
        Assert.Equal("a,near", SeatsOf(room));
        Assert.NotNull(h.Mm.Queue.Get("far"));
    }

    [Fact]
    public void Rated_rooms_without_join_in_progress_are_never_joined()
    {
        var p = Playlist("r", new RoomRules { OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot, BotRefill = false }, rated: true);
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        h.Host.Return("c-b");
        h.Connect("c");
        h.Queue("c", p);
        h.Run(1);
        Assert.Equal("a,open", SeatsOf(room));
    }

    // ------------------------------------------------------------------ lobby rooms and disconnects

    private static readonly PlaylistOptions Any = Playlist("any", new RoomRules());

    [Fact]
    public void A_lobby_room_survives_everyone_dropping_and_the_host_keeps_the_role()
    {
        var h = new Harness(new[] { Any });
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var lobby = h.Lobbies.LobbyOf(a)!;
        h.Lobbies.Join(b, lobby.Code);
        h.Lobbies.Start(a, "duo");
        var room = Assert.Single(h.Host.Rooms);

        h.Host.Disconnect("c-a"); // the host drops first
        Assert.True(lobby.IsHost("a"));
        Assert.Equal("lobby:5:2:guest:True", h.Game.To("c-b").Last(x => x.StartsWith("lobby"))); // still two members, the room runs
        h.Host.Disconnect("c-b");
        h.Run(3);
        Assert.Single(h.Host.Rooms);
        Assert.Contains(lobby, h.Lobbies.Lobbies);

        var a2 = h.Connect("a", client: "c-a2");
        Assert.Same(room, a2.Room);
        Assert.Same(lobby, h.Lobbies.LobbyOf(a2));
        Assert.True(lobby.IsHost("a"));
        Assert.Contains("lobby:5:2:host:True", h.Game.To("c-a2"));

        // b never comes back: after the grace it leaves the lobby; the room goes on with a.
        h.Run(2.1);
        Assert.Single(h.Host.Rooms);
        Assert.Equal(new[] { "a" }, lobby.Members.Select(m => m.PrincipalId));
        Assert.Equal("lobby:5:1:host:True", h.Game.To("c-a2").Last(x => x.StartsWith("lobby")));
    }

    [Fact]
    public void A_lobby_whose_members_never_come_back_closes_with_its_room()
    {
        var h = new Harness(new[] { Any });
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var lobby = h.Lobbies.LobbyOf(a)!;
        h.Lobbies.Join(b, lobby.Code);
        h.Lobbies.Start(a, "duo");
        h.Host.Disconnect("c-a");
        h.Host.Disconnect("c-b");
        h.Run(5.1);
        Assert.Empty(h.Host.Rooms);
        Assert.Empty(h.Lobbies.Lobbies);
    }

    [Fact]
    public void A_dropped_host_returning_after_the_room_ended_finds_the_role_moved_on()
    {
        var h = new Harness(new[] { Any }, new RoomHostOptions { ReconnectGraceSeconds = 30, ReturnAfterEndSeconds = 0.5 });
        h.Game.EndAfter = 60;
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var lobby = h.Lobbies.LobbyOf(a)!;
        h.Lobbies.Join(b, lobby.Code);
        h.Lobbies.Start(a, "duo");
        h.Host.Disconnect("c-a");
        h.Run(2); // the room ended and went
        Assert.Empty(h.Host.Rooms);
        Assert.Equal(new[] { "b" }, lobby.Members.Select(m => m.PrincipalId));
        Assert.True(lobby.IsHost("b"));
        Assert.Equal("lobby:5:1:host:False", h.Game.To("c-b").Last());
    }

    // ------------------------------------------------------------------ lobby team sizes

    [Fact]
    public void A_lobby_cannot_start_a_mode_its_members_do_not_fit()
    {
        var h = new Harness(new[] { Any });
        var members = new[] { "a", "b", "c" }.Select(w => h.Connect(w)).ToList();
        h.Lobbies.Join(members[0], "");
        var code = h.Lobbies.LobbyOf(members[0])!.Code;
        foreach (var s in members.Skip(1)) h.Lobbies.Join(s, code);
        h.Lobbies.Start(members[0], "duo"); // 1 per team: 2 seats for 3 members
        Assert.Empty(h.Host.Rooms);
        Assert.Contains("reject:TooManyPlayers", h.Game.To("c-a"));

        // A mode without fixed teams seats everyone, without bots.
        h.Lobbies.Start(members[0], "free");
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(3, room.Seats.Count);
        Assert.DoesNotContain(room.Seats, s => s.IsBot);
    }

    // ------------------------------------------------------------------ the connection's state

    [Fact]
    public void A_player_in_a_room_or_joining_one_cannot_queue()
    {
        var p = Playlist("p", new RoomRules());
        var h = new Harness(new[] { p });
        var room = h.Pair(p);
        Assert.False(h.Queue("a", p));
        Assert.Null(h.Mm.Queue.Get("a"));
        Assert.Single(h.Host.Rooms);
        Assert.Same(room, h.Host.SessionOf("c-a")!.Room);
    }

    [Fact]
    public void Queueing_leaves_the_lobby_and_joining_a_lobby_leaves_the_queue()
    {
        var p = Playlist("p", new RoomRules());
        var h = new Harness(new[] { p });
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var lobby = h.Lobbies.LobbyOf(a)!;
        h.Lobbies.Join(b, lobby.Code);

        Assert.True(h.Queue("b", p));
        Assert.Null(h.Lobbies.LobbyOf(b));
        Assert.Equal(new[] { "a" }, lobby.Members.Select(m => m.PrincipalId));
        Assert.Equal("lobby:5:1:host:False", h.Game.To("c-a").Last());

        h.Lobbies.Join(b, lobby.Code);
        Assert.Null(h.Mm.Queue.Get("b"));
        Assert.Contains("queue:Idle:p:0/0", h.Game.To("c-b"));
        Assert.Same(lobby, h.Lobbies.LobbyOf(b));
        h.Run(10);
        Assert.Empty(h.Host.Rooms); // nobody is left in the queue to fill a room with bots
    }

    [Fact]
    public void Joining_another_lobby_leaves_the_first_and_joining_the_same_one_changes_nothing()
    {
        var h = new Harness(new[] { Any });
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var first = h.Lobbies.LobbyOf(a)!;
        h.Lobbies.Join(b, "");
        var second = h.Lobbies.LobbyOf(b)!;

        h.Lobbies.Join(a, first.Code.ToLowerInvariant());
        Assert.Single(first.Members);

        h.Lobbies.Join(a, second.Code);
        Assert.Same(second, h.Lobbies.LobbyOf(a));
        Assert.DoesNotContain(first, h.Lobbies.Lobbies); // emptied and closed
        Assert.Equal(2, second.Members.Count);
        Assert.True(second.IsHost("b"));
    }

    [Fact]
    public void A_player_in_a_room_cannot_join_a_lobby()
    {
        var p = Playlist("p", new RoomRules());
        var h = new Harness(new[] { p });
        h.Pair(p);
        var a = h.Host.SessionOf("c-a")!;
        h.Lobbies.Join(a, "");
        Assert.Null(h.Lobbies.LobbyOf(a));
        Assert.Empty(h.Lobbies.Lobbies);
    }

    // ------------------------------------------------------------------ starting rooms

    [Fact]
    public void TryStart_respects_the_capacity_and_the_drain_and_Start_does_not()
    {
        using var node = new ServerNode(new ServerNodeOptions { MaxLoad = 1 }, nodeId: "n");
        var h = new Harness(new[] { Any });
        h.Host.UseServer(node);
        var first = h.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules());
        Assert.True(h.Host.TryStart(first));
        Assert.False(h.Host.TryStart(first)); // already running
        var second = h.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules());
        Assert.False(h.Host.TryStart(second)); // full
        Assert.Single(h.Host.Rooms);
        h.Host.Start(second); // tools may start a room anyway
        Assert.Equal(2, h.Host.Rooms.Count);

        var free = new Harness(new[] { Any });
        free.Host.BeginDrain();
        Assert.False(free.Host.TryStart(free.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules())));
        Assert.Empty(free.Host.Rooms);
    }

    // ------------------------------------------------------------------ ended rooms

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ended_rooms_step_on_only_when_the_options_say_so(bool stepAfterEnd)
    {
        var p = Playlist("p", new RoomRules());
        var h = new Harness(new[] { p }, new RoomHostOptions { StepAfterEnd = stepAfterEnd, ReturnAfterEndSeconds = 1 });
        h.Game.EndAfter = 30;
        var room = h.Pair(p);
        h.Run(0.8);
        Assert.True(room.Finished);
        Assert.InRange(room.EndedSeconds, 0.2, 0.45);
        if (stepAfterEnd) Assert.True(room.Sim.Steps > 30);
        else Assert.Equal(30, room.Sim.Steps);
        // Snapshots go on either way, and the room goes after ReturnAfterEndSeconds.
        var snaps = h.Game.To("c-a").Count(x => x.StartsWith("snap"));
        h.Run(0.1);
        Assert.True(h.Game.To("c-a").Count(x => x.StartsWith("snap")) > snaps);
        h.Run(1);
        Assert.Empty(h.Host.Rooms);
    }
}
