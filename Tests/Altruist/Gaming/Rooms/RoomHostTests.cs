using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Lobbies;
using Altruist.Gaming.Matchmaking;
using Altruist.Gaming.Rooms;

namespace Tests.Gaming.Rooms;

// A tiny game to drive the generic hosting: every seat walks along a line by its input.

public readonly record struct Walk(int Dx);

public sealed class LineSim : IRoomSimulation<Walk>
{
    public readonly Dictionary<int, (int Team, int X, bool Bot)> Seats = new();
    public int Steps;
    public int EndAfter = int.MaxValue;
    public float Dt => 1f / 60f;
    public bool Ended => Steps >= EndAfter;
    public void AddSeat(int seat, int team) => Seats[seat] = (team, 0, false);
    public void RemoveSeat(int seat) => Seats.Remove(seat);
    public void SetBotControlled(int seat, bool bot) => Seats[seat] = Seats[seat] with { Bot = bot };

    public void Step(IReadOnlyDictionary<int, Walk> inputs)
    {
        foreach (var (id, w) in inputs) Seats[id] = Seats[id] with { X = Seats[id].X + w.Dx };
        Steps++;
    }
}

public sealed record Player(string Id, string Name, int Rating = 1000);

public sealed class Sent(string kind, string text) : IPacketBase
{
    public uint MessageCode { get; set; }
    public string Kind { get; } = kind;
    public string Text { get; } = text;
    public override string ToString() => $"{Kind}:{Text}";
}

public sealed class LineGame :
    IRoomGame<LineSim, Walk, Player>, IMatchmakingGame<LineSim, Walk, Player>, ILobbyGame<LineSim, Walk, Player>, IRoomTransport
{
    public readonly List<(string Client, Sent Packet)> Out = new();
    public readonly List<string> Closed = new();
    public readonly List<string> Abandons = new();
    public readonly List<string> Finished = new();
    public readonly List<string> ForcedClosed = new();
    public readonly List<string> Draws = new();
    public int TeamForfeits;
    /// <summary>The game ends the simulation when a team forfeits (default); false: it keeps running.</summary>
    public bool EndOnForfeit = true;
    /// <summary>Load of a room per mode (capacity tests); 1 by default.</summary>
    public Func<string, double> Load = _ => 1;
    public int EndAfter = int.MaxValue;
    private int _bots;

    public List<string> To(string client) => Out.Where(o => o.Client == client).Select(o => o.Packet.ToString()).ToList();

    public void Send(string clientId, IPacketBase packet) => Out.Add((clientId, (Sent)packet));
    public void Disconnect(string clientId) => Closed.Add(clientId);

    public Walk NeutralInput => default;
    public string NameOf(Player player) => player.Name;
    public string BotNameOf(Room<LineSim, Walk, Player> room, Seat<LineSim, Walk, Player> seat) => $"bot{seat.Id}";
    public LineSim CreateSimulation(string mode, string? playlist) => new() { EndAfter = EndAfter };
    public double LoadOf(string mode, string? playlist) => Load(mode);
    public void OnForcedClose(Room<LineSim, Walk, Player> room) => ForcedClosed.Add(room.Id);
    public IPacketBase? ServerDraining() => new Sent("draining", "");
    /// <summary>Fleet tests turn redirects on; the single-server tests keep the default (no redirect packet).</summary>
    public bool Redirects;
    public IPacketBase? Redirect(RoomRedirect r) => Redirects ? new Sent("redirect", $"{r.NodeId}:{r.Reason}") : null;
    public ISeatController<Walk> CreateBot(Room<LineSim, Walk, Player> room, Seat<LineSim, Walk, Player> seat) => new Stepper(++_bots);
    public void OnAbandoned(Room<LineSim, Walk, Player> room, Participant<LineSim, Walk, Player> p) => Abandons.Add(p.PrincipalId);
    public void OnFinished(Room<LineSim, Walk, Player> room) => Finished.Add(room.Id);
    public void OnTeamForfeit(Room<LineSim, Walk, Player> room, int winnerTeam)
    {
        TeamForfeits++;
        if (EndOnForfeit) room.Sim.EndAfter = room.Sim.Steps;
    }

    public void OnForfeitDraw(Room<LineSim, Walk, Player> room) => Draws.Add(room.Id);

    public IPacketBase? Welcome(Room<LineSim, Walk, Player> room, Participant<LineSim, Walk, Player> p) => new Sent("welcome", $"{p.Seat!.Id}");
    public IPacketBase? Roster(Room<LineSim, Walk, Player> room) => new Sent("roster", string.Join(",", room.Seats.Select(s => s.Owner?.PrincipalId ?? (s.IsBot ? "bot" : "open"))));
    public IPacketBase? Notice(Room<LineSim, Walk, Player> room, RoomNoticeKind kind, int seat, string name) => new Sent("notice", $"{kind}:{seat}:{name}");
    public object CaptureSnapshot(Room<LineSim, Walk, Player> room) => room.Sim.Steps;
    public IPacketBase PersonalizeSnapshot(object shared, Participant<LineSim, Walk, Player> p) => new Sent("snap", $"{shared}:ack{p.Input.LastAppliedSeq}");
    public IPacketBase? ReturnedToMenu(Room<LineSim, Walk, Player> room) => new Sent("menu", room.Playlist ?? "");
    public IPacketBase? NothingToRejoin() => new Sent("concluded", "");

    public int RatingOf(Player player, PlaylistOptions playlist) => player.Rating;
    public double TimeLeftSeconds(Room<LineSim, Walk, Player> room) => 120;
    public IPacketBase QueueStatus(QueueStatusInfo s) => new Sent("queue", $"{s.State}:{s.Playlist}:{s.PlayersFound}/{s.PlayersNeeded}");

    public int? TeamSizeOf(string mode) => mode switch { "duo" => 1, "free" => 0, _ => null };
    public IPacketBase LobbyState(Lobby<LineSim, Walk, Player> lobby, LobbyMember<Player> to) =>
        new Sent("lobby", $"{lobby.Code.Length}:{lobby.Members.Count}:{(lobby.IsHost(to.PrincipalId) ? "host" : "guest")}:{lobby.Room is not null}");
    public IPacketBase Rejected(LobbyRejectReason reason, string code) => new Sent("reject", reason.ToString());

    private sealed class Stepper(int n) : ISeatController<Walk>
    {
        public Walk Think() => new(n);
    }
}

public class RoomHostTests
{
    private static readonly PlaylistOptions Casual = new()
    {
        Id = "casual",
        Mode = "duel",
        TeamSize = 1,
        Rules = new RoomRules
        {
            OnDisconnect = DisconnectAction.BotAfterDelay,
            OnGraceExpired = GraceExpiredAction.ForfeitRejoin,
            OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot,
            ReclaimTeamSeat = true,
            JoinInProgress = true,
            BotRefill = true,
        },
    };

    private static readonly PlaylistOptions Rated = new()
    {
        Id = "rated",
        Mode = "duel",
        TeamSize = 1,
        Rated = true,
        Rules = new RoomRules
        {
            OnGraceExpired = GraceExpiredAction.Abandon,
            OnVoluntaryLeave = VoluntaryLeaveAction.Abandon,
            WhenEmpty = EmptyRoomAction.TeamForfeit,
        },
    };

    private sealed class Harness
    {
        public readonly LineGame Game = new();
        public readonly RoomHost<LineSim, Walk, Player> Host;
        public readonly MatchmakingModule<LineSim, Walk, Player> Mm;
        public readonly LobbyModule<LineSim, Walk, Player> Lobbies;
        private readonly WorldCoordinator _world;

        public Harness(double grace = 30)
        {
            Host = new RoomHost<LineSim, Walk, Player>(Game, Game, new RoomHostOptions { ReconnectGraceSeconds = grace }, 60);
            Mm = new MatchmakingModule<LineSim, Walk, Player>(Game, new MatchmakingOptions(), new[] { Casual, Rated });
            Lobbies = new LobbyModule<LineSim, Walk, Player>(Game, new LobbyOptions());
            Host.Use(Mm).Use(Lobbies);
            _world = new WorldCoordinator(new IWorldStepper[] { Host });
        }

        public RoomSession<LineSim, Walk, Player> Connect(string who, int rating = 1000, string? client = null) =>
            Host.Connect(client ?? "c-" + who, who, new Player(who, who.ToUpperInvariant(), rating))!;

        public void Queue(string who, PlaylistOptions playlist, string? client = null) => Mm.Enqueue(Host.SessionOf(client ?? "c-" + who)!, playlist);

        public void Run(double seconds)
        {
            for (var i = 0; i < (int)Math.Round(seconds * 60); i++) _world.Step(1f / 60f);
        }
    }

    [Fact]
    public void A_lone_player_is_filled_with_bots_after_the_wait()
    {
        var h = new Harness();
        h.Connect("a");
        h.Queue("a", Casual);
        h.Run(3);
        Assert.Empty(h.Host.Rooms);
        h.Run(3.1);
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(new[] { "a", "bot" }, room.Seats.Select(s => s.Owner?.PrincipalId ?? (s.IsBot ? "bot" : "open")));
        Assert.Contains("queue:Found:casual:2/2", h.Game.To("c-a"));
        Assert.Contains("welcome:1", h.Game.To("c-a"));
        Assert.True(room.Sim.Seats[2].Bot);
    }

    [Fact]
    public void Inputs_are_applied_one_per_step_and_acked_in_snapshots()
    {
        var h = new Harness();
        h.Connect("a");
        h.Connect("b");
        h.Queue("a", Casual);
        h.Queue("b", Casual);
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        var seat = room.Participants["a"].Seat!.Id;
        var x0 = room.Sim.Seats[seat].X;
        for (var seq = 1; seq <= 4; seq++) h.Host.SubmitInput("c-a", seq, new Walk(10));
        h.Host.SubmitInput("c-a", 2, new Walk(1000)); // stale
        h.Run(4 / 60.0);
        Assert.Equal(x0 + 40, room.Sim.Seats[seat].X);
        h.Run(2 / 60.0); // starved: the last input repeats
        Assert.Equal(x0 + 60, room.Sim.Seats[seat].X);
        Assert.Contains(h.Game.To("c-a"), p => p.EndsWith(":ack4"));
    }

    [Fact]
    public void A_dropped_player_gets_a_bot_then_reclaims_a_team_seat()
    {
        var h = new Harness();
        h.Connect("a");
        h.Connect("b");
        h.Queue("a", Casual);
        h.Queue("b", Casual);
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        h.Host.Disconnect("c-b");
        Assert.Contains($"notice:Left:{room.Participants["b"].LastSeat}:B", h.Game.To("c-a"));
        h.Run(2.1);
        Assert.Null(room.Participants["b"].Seat);
        Assert.Contains(room.Seats, s => s.IsBot);
        Assert.NotNull(h.Host.ActiveRoomOf("b"));
        h.Connect("b", client: "c-b2");
        Assert.NotNull(room.Participants["b"].Seat);
        Assert.Equal("c-b2", room.Participants["b"].ClientId);
        Assert.Contains(h.Game.To("c-a"), p => p.StartsWith("notice:Reconnected"));
        Assert.Contains(h.Game.To("c-b2"), p => p.StartsWith("welcome"));
    }

    [Fact]
    public void Leaving_on_purpose_opens_the_seat_for_a_queued_player()
    {
        var h = new Harness();
        h.Connect("a");
        h.Connect("b");
        h.Queue("a", Casual);
        h.Queue("b", Casual);
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        h.Host.Return("c-b");
        Assert.Contains("menu:casual", h.Game.To("c-b"));
        h.Connect("c");
        h.Queue("c", Casual);
        h.Run(0.05);
        Assert.Contains(h.Game.To("c-a"), p => p.StartsWith("notice:Joining") && p.EndsWith(":C"));
        h.Run(2.1);
        Assert.Contains(room.Seats, s => s.Owner?.PrincipalId == "c");
        Assert.Single(h.Host.Rooms);
    }

    [Fact]
    public void Rated_groups_form_inside_the_rating_window_and_a_no_show_abandons()
    {
        var h = new Harness(grace: 5);
        h.Connect("a", rating: 1000);
        h.Connect("b", rating: 1150);
        h.Queue("a", Rated);
        h.Queue("b", Rated);
        h.Run(1);
        Assert.Empty(h.Host.Rooms);
        h.Run(1.1); // window 150 after two seconds
        var room = Assert.Single(h.Host.Rooms);
        Assert.DoesNotContain(room.Seats, s => s.IsBot);
        h.Host.Disconnect("c-a");
        h.Run(5.1);
        Assert.Equal(new[] { "a" }, h.Game.Abandons);
        Assert.Single(room.Seats);
        Assert.True(room.Sim.Ended);
        Assert.Equal(new[] { room.Id }, h.Game.Finished);
        h.Run(6.1);
        Assert.Empty(h.Host.Rooms);
        Assert.Contains("menu:rated", h.Game.To("c-b"));
    }

    [Fact]
    public void The_newest_connection_of_a_principal_replaces_the_old_one()
    {
        var h = new Harness();
        h.Connect("a");
        h.Queue("a", Casual);
        h.Connect("a", client: "c-a2");
        Assert.Equal(new[] { "c-a" }, h.Game.Closed);
        Assert.Null(h.Mm.Queue.Get("a"));
        Assert.Equal(1, h.Host.SessionCount);
    }

    [Fact]
    public void A_lobby_starts_a_room_fills_bots_and_comes_back_after_it()
    {
        var h = new Harness();
        var a = h.Connect("a");
        var b = h.Connect("b");
        h.Lobbies.Join(a, "");
        var code = h.Lobbies.LobbyOf(a)!.Code;
        h.Lobbies.Join(b, code.ToLowerInvariant());
        h.Lobbies.Join(h.Connect("x"), "NOPE1");
        Assert.Contains("reject:NotFound", h.Game.To("c-x"));
        Assert.Contains("lobby:5:2:guest:False", h.Game.To("c-b"));
        h.Lobbies.Start(b, "duo"); // not the host
        Assert.Empty(h.Host.Rooms);
        h.Lobbies.Start(a, "duo");
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(code, room.LobbyCode);
        Assert.Equal(2, room.Seats.Count);
        Assert.Contains("lobby:5:2:host:True", h.Game.To("c-a"));
        // A guest steps out; the host ends the room for everyone.
        h.Host.Return("c-b");
        Assert.Single(h.Host.Rooms);
        h.Host.Return("c-a");
        Assert.Empty(h.Host.Rooms);
        Assert.Equal("lobby:5:2:host:False", h.Game.To("c-a").Last());
        Assert.DoesNotContain(h.Game.To("c-a"), p => p.StartsWith("menu"));
    }

    [Fact]
    public void A_connection_with_nothing_going_on_is_told_at_once()
    {
        var h = new Harness();
        h.Connect("a");
        h.Host.RejoinRequest("c-a");
        Assert.Equal(new[] { "concluded:" }, h.Game.To("c-a"));
        h.Queue("a", Casual);
        h.Host.RejoinRequest("c-a");
        Assert.Single(h.Game.To("c-a"), p => p == "concluded:");
    }

    [Fact]
    public void Input_buffer_drops_stale_and_overflowing_inputs()
    {
        var b = new InputBuffer<Walk>(2, default);
        Assert.True(b.Offer(1, new Walk(1)));
        Assert.False(b.Offer(1, new Walk(9)));
        Assert.True(b.Offer(2, new Walk(2)));
        Assert.True(b.Offer(3, new Walk(3)));
        Assert.Equal(2, b.Depth);
        Assert.Equal(new Walk(2), b.Consume());
        Assert.Equal(new Walk(3), b.Consume());
        Assert.Equal(new Walk(3), b.Consume());
        Assert.Equal(3, b.LastAppliedSeq);
        Assert.False(b.Offer(3, new Walk(4)));
        b.Reset();
        Assert.Equal(default, b.Consume());
    }

    [Fact]
    public void Teams_balance_by_rating()
    {
        var players = new[] { 1000, 1100, 1200, 1300 }.Select((r, i) => new QueueEntry($"p{i}", "rated", r, i)).ToList();
        var (t0, t1) = Matchmaker.BalanceTeams(players, byRating: true);
        Assert.Equal(t0.Sum(p => p.Rating), t1.Sum(p => p.Rating));
        Assert.Equal(100, Matchmaker.RatingWindow(0, new MatchmakingOptions()));
        Assert.Equal(500, Matchmaker.RatingWindow(60, new MatchmakingOptions()));
    }
}
