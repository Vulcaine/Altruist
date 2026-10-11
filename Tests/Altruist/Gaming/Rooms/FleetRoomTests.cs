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
/// Several servers in one process, sharing one in-memory backplane, each with its own room host,
/// matchmaking and lobbies; simulated clients follow redirects the way a real client does
/// (reconnect to the named server and say "rejoin").
/// </summary>
public sealed class Cluster
{
    public static readonly PlaylistOptions Duel = new()
    {
        Id = "duel",
        Mode = "duel",
        TeamSize = 1,
        Rules = new RoomRules { BotRefill = true, OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot, OnDisconnect = DisconnectAction.Hold },
    };

    public readonly InMemoryFleetBackplane Backplane = new(shared: true);
    public readonly List<Server> Servers = new();
    /// <summary>Where each player's client is connected now.</summary>
    public readonly Dictionary<string, (Server Server, string Client)> Clients = new();
    public readonly List<(string Player, string From, string To, string Reason)> Moves = new();
    private int _generation;
    public bool FollowRedirects = true;

    public sealed class Server
    {
        public required string Id;
        public required LineGame Game;
        public required ServerNode Node;
        public required Fleet Fleet;
        public required RoomHost<LineSim, Walk, Player> Host;
        public required MatchmakingModule<LineSim, Walk, Player> Mm;
        public required LobbyModule<LineSim, Walk, Player> Lobbies;
        public required WorldCoordinator World;
        public int Seen;
        public List<string> To(string client) => Game.To(client);
    }

    public Server Add(string id, double maxLoad = 0, int endAfter = int.MaxValue, bool redirects = true, TimeSpan? drainTimeout = null)
    {
        var game = new LineGame { Redirects = redirects, EndAfter = endAfter };
        var node = new ServerNode(new ServerNodeOptions
        {
            MaxLoad = maxLoad,
            DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30),
            PollInterval = TimeSpan.FromMilliseconds(5),
        }, nodeId: id);
        var fleet = new Fleet(Backplane, node, new FleetOptions { Cluster = "rooms", InternalAddress = $"{id}:8080" });
        var host = new RoomHost<LineSim, Walk, Player>(game, game, new RoomHostOptions { ReturnAfterEndSeconds = 1, ReconnectGraceSeconds = 30 }, 60);
        var mm = new MatchmakingModule<LineSim, Walk, Player>(game, new MatchmakingOptions(), new[] { Duel });
        var lobbies = new LobbyModule<LineSim, Walk, Player>(game, new LobbyOptions());
        host.Use(mm).Use(lobbies).UseServer(node).UseFleet(fleet);
        var server = new Server { Id = id, Game = game, Node = node, Fleet = fleet, Host = host, Mm = mm, Lobbies = lobbies, World = new WorldCoordinator(new IWorldStepper[] { host }) };
        Servers.Add(server);
        return server;
    }

    public Server this[string id] => Servers.First(s => s.Id == id);

    public async Task Beat()
    {
        foreach (var s in Servers) await s.Fleet.HeartbeatAsync();
        foreach (var s in Servers) await s.Fleet.HeartbeatAsync();
    }

    public RoomSession<LineSim, Walk, Player> Connect(string player, Server on, int rating = 1000)
    {
        var client = $"c-{player}-{++_generation}";
        Clients[player] = (on, client);
        return on.Host.Connect(client, player, new Player(player, player.ToUpperInvariant(), rating))!;
    }

    public RoomSession<LineSim, Walk, Player> SessionOf(string player) => Clients[player].Server.Host.SessionOf(Clients[player].Client)!;

    public List<string> To(string player) => Clients[player].Server.To(Clients[player].Client);

    public void Queue(string player) => Clients[player].Server.Mm.Enqueue(SessionOf(player), Duel);

    public void Disconnect(string player) => Clients[player].Server.Host.Disconnect(Clients[player].Client);

    /// <summary>One frame on every server, then clients follow the redirects they got.</summary>
    public void Frame()
    {
        foreach (var s in Servers) s.World.Step(1f / 60f);
        if (FollowRedirects) Follow();
    }

    private void Follow()
    {
        foreach (var s in Servers)
        {
            var outbox = s.Game.Out;
            for (; s.Seen < outbox.Count; s.Seen++)
            {
                var (client, packet) = outbox[s.Seen];
                if (packet.Kind != "redirect") continue;
                var player = Clients.First(c => c.Value.Client == client).Key;
                var parts = packet.Text.Split(':');
                var target = this[parts[0]];
                Moves.Add((player, s.Id, target.Id, parts[1]));
                Assert.Contains(client, s.Game.Closed); // the old connection is closed by the server
                var session = Connect(player, target);
                target.Host.RejoinRequest(Clients[player].Client);
                _ = session;
            }
        }
    }

    /// <summary>Runs frames until no server has fleet work in flight (lookups, handovers) and nobody moved.</summary>
    public async Task Settle(int maxFrames = 600)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            // Async lookups and handovers in flight: give them a moment; a frame runs what they posted.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Servers.Any(s => s.Host.PendingAsync > 0) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1);
                Frame();
            }
            var moves = Moves.Count;
            Frame();
            if (Servers.All(s => s.Host.PendingAsync == 0) && Moves.Count == moves) return;
        }
        throw new TimeoutException("the cluster did not settle");
    }

    public async Task Run(double seconds)
    {
        var frames = (int)Math.Round(seconds * 60);
        for (var i = 0; i < frames; i++)
        {
            Frame();
            if (i % 30 == 29) await Beat();
        }
        await Settle();
    }
}

public class FleetRoomTests
{
    private static async Task<Cluster> TwoServers(double maxLoadA = 0, double maxLoadB = 0, int endAfter = int.MaxValue, bool redirects = true)
    {
        var c = new Cluster();
        c.Add("a", maxLoadA, endAfter, redirects);
        c.Add("b", maxLoadB, endAfter, redirects);
        await c.Beat();
        return c;
    }

    private static Room<LineSim, Walk, Player>? RoomOf(Cluster c, string player) =>
        c.Servers.SelectMany(s => s.Host.Rooms).FirstOrDefault(r => r.Participants.ContainsKey(player));

    // ------------------------------------------------------------------ one server: nothing changes

    [Fact]
    public async Task A_fleet_of_one_matches_locally_and_moves_nobody()
    {
        var c = new Cluster();
        var a = c.Add("a");
        await c.Beat();
        c.Connect("p1", a);
        c.Connect("p2", a);
        c.Queue("p1");
        c.Queue("p2");
        await c.Run(0.5);
        Assert.Empty(c.Moves);
        Assert.Single(a.Host.Rooms);
        Assert.Equal(0, a.Fleet.Self.Stat("queue:duel")); // published: nobody waits now
    }

    [Fact]
    public async Task Without_a_redirect_packet_every_player_stays_where_it_connected()
    {
        var c = await TwoServers(redirects: false);
        c.Connect("p1", c["a"]);
        c.Connect("p2", c["b"]);
        c.Queue("p1");
        c.Queue("p2");
        await c.Run(2);
        Assert.Empty(c.Moves);
        Assert.Empty(c["a"].Host.Rooms); // each waits alone (bots fill after 6 s)
        Assert.Empty(c["b"].Host.Rooms);
    }

    // ------------------------------------------------------------------ queues gather on one server

    [Fact]
    public async Task Players_queued_on_different_servers_meet_on_one_and_keep_their_waiting_time()
    {
        var c = await TwoServers();
        c.Connect("p1", c["a"]);
        c.Queue("p1");
        await c.Run(2); // p1 waits 2 s alone on a
        c.Connect("p2", c["b"]);
        c.Queue("p2");
        await c.Run(0.5);

        // b had one waiting, a had one: the tie goes to the lowest id, so p2 moves to a.
        Assert.Equal(new[] { ("p2", "b", "a", "queue") }, c.Moves.Select(m => (m.Player, m.From, m.To, m.Reason)));
        var room = Assert.Single(c["a"].Host.Rooms);
        Assert.Equal(new[] { "p1", "p2" }, room.Participants.Keys.OrderBy(k => k));
        Assert.Empty(c["b"].Host.Rooms);
        Assert.Contains("queue:Found:duel:2/2", c.To("p2"));
    }

    [Fact]
    public async Task The_server_with_more_players_waiting_wins_and_waiting_time_moves_along()
    {
        var c = await TwoServers();
        c.Connect("b1", c["b"]);
        c.Queue("b1");
        await c.Run(4); // b1 has waited 4 s on b
        c.Connect("a1", c["a"]);
        c.Queue("a1");
        await c.Beat();
        // Tie (1 and 1): the lowest id, a, gathers; b1 moves there (b looks again every second).
        await c.Run(1.2);
        Assert.Contains(c.Moves, m => m.Player == "b1" && m.To == "a");
        Assert.NotNull(RoomOf(c, "b1"));
        Assert.Same(RoomOf(c, "a1"), RoomOf(c, "b1"));
    }

    [Fact]
    public async Task A_full_server_sends_its_queue_to_one_with_room()
    {
        var c = await TwoServers(maxLoadA: 1);
        var a = c["a"];
        a.Host.Start(a.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules())); // a is full
        await c.Beat();
        c.Connect("p1", a);
        c.Connect("p2", a);
        c.Queue("p1");
        c.Queue("p2");
        await c.Run(0.5);
        Assert.Equal(2, c.Moves.Count(m => m.To == "b"));
        var room = Assert.Single(c["b"].Host.Rooms);
        Assert.Equal(2, room.Participants.Count);
    }

    [Fact]
    public async Task When_no_server_has_room_the_players_wait_where_they_are()
    {
        var c = await TwoServers(maxLoadA: 1, maxLoadB: 1);
        foreach (var s in c.Servers) s.Host.Start(s.Host.CreateRoom(new LineSim(), "duel", null, null, new RoomRules()));
        await c.Beat();
        c.Connect("p1", c["a"]);
        c.Queue("p1");
        await c.Run(1);
        Assert.Empty(c.Moves);
        Assert.NotNull(c["a"].Mm.Queue.Get("p1"));
    }

    [Fact]
    public async Task A_player_reconnecting_to_another_server_is_sent_back_to_its_room()
    {
        var c = await TwoServers();
        c.Connect("p1", c["a"]);
        c.Connect("p2", c["a"]);
        c.Queue("p1");
        c.Queue("p2");
        await c.Run(0.5);
        var room = Assert.Single(c["a"].Host.Rooms);

        c.Disconnect("p1");
        c.Connect("p1", c["b"]); // the load balancer picked b
        c["b"].Host.RejoinRequest(c.Clients["p1"].Client);
        Assert.DoesNotContain("concluded:", c.To("p1")); // not answered before the lookup
        await c.Settle();

        Assert.Contains(c.Moves, m => m == ("p1", "b", "a", "rejoin"));
        Assert.Same(c["a"], c.Clients["p1"].Server);
        Assert.Same(room, c.SessionOf("p1").Room);
        Assert.Contains("welcome:1", c.To("p1"));
    }

    [Fact]
    public async Task Nothing_to_rejoin_is_answered_after_the_lookup()
    {
        var c = await TwoServers();
        c.Connect("p", c["b"]);
        c["b"].Host.RejoinRequest(c.Clients["p"].Client);
        Assert.Empty(c.To("p"));
        await c.Settle();
        Assert.Equal(new[] { "concluded:" }, c.To("p"));
        Assert.Empty(c.Moves);
    }

    [Fact]
    public async Task A_room_on_a_crashed_server_is_not_chased()
    {
        var c = await TwoServers();
        c.Connect("p1", c["a"]);
        c.Connect("p2", c["a"]);
        c.Queue("p1");
        c.Queue("p2");
        await c.Run(0.5);
        Assert.Single(c["a"].Host.Rooms);
        await c["a"].Fleet.LeaveAsync(); // a is gone (as after a crash, once its heartbeat expired)
        await c["b"].Fleet.HeartbeatAsync();
        c.Connect("p1", c["b"]);
        c["b"].Host.RejoinRequest(c.Clients["p1"].Client);
        await c.Settle();
        Assert.Empty(c.Moves);
        Assert.Equal(new[] { "concluded:" }, c.To("p1"));
    }

    // ------------------------------------------------------------------ lobbies by code anywhere

    [Fact]
    public async Task A_lobby_code_joined_on_another_server_leads_to_the_lobby()
    {
        var c = await TwoServers();
        var host = c.Connect("h", c["a"]);
        c["a"].Lobbies.Join(host, "");
        var code = c["a"].Lobbies.LobbyOf(host)!.Code;
        var guest = c.Connect("g", c["b"]);
        c["b"].Lobbies.Join(guest, code.ToLowerInvariant());
        await c.Settle();
        Assert.Contains(c.Moves, m => m == ("g", "b", "a", "lobby"));
        var lobby = c["a"].Lobbies.Lobbies.Single();
        Assert.Equal(new[] { "h", "g" }, lobby.Members.Select(m => m.PrincipalId));
        Assert.True(lobby.IsHost("h"));
        Assert.Contains("lobby:5:2:guest:False", c.To("g"));
    }

    [Fact]
    public async Task An_unknown_code_is_rejected_after_the_fleet_was_asked()
    {
        var c = await TwoServers();
        var g = c.Connect("g", c["b"]);
        c["b"].Lobbies.Join(g, "ZZZZZ");
        Assert.Empty(c.To("g"));
        await c.Settle();
        Assert.Equal(new[] { "reject:NotFound" }, c.To("g"));
    }

    // ------------------------------------------------------------------ draining into the fleet

    [Fact]
    public async Task A_draining_server_hands_its_queue_and_lobbies_to_another_and_keeps_its_rooms()
    {
        var c = await TwoServers(endAfter: 300);
        var a = c["a"];
        // A running room on a.
        c.Connect("r1", a);
        c.Connect("r2", a);
        c.Queue("r1");
        c.Queue("r2");
        await c.Run(0.3);
        var room = Assert.Single(a.Host.Rooms);
        // A waiting player and a lobby (host + guest) on a.
        c.Connect("q", a);
        c.Queue("q");
        var lh = c.Connect("lh", a);
        a.Lobbies.Join(lh, "");
        var code = a.Lobbies.LobbyOf(lh)!.Code;
        a.Lobbies.Join(c.Connect("lg", a), code);
        await c.Run(1.5);
        var waited = a.Host.Now - a.Mm.Queue.Get("q")!.QueuedAt;

        var drain = a.Node.DrainAsync();
        await c.Beat();
        await c.Run(0.2);

        // The queue and the lobby moved to b; the lobby keeps its code and host.
        Assert.Contains(c.Moves, m => m == ("q", "a", "b", "drain"));
        Assert.Contains(c.Moves, m => m == ("lh", "a", "b", "drain"));
        Assert.Contains(c.Moves, m => m == ("lg", "a", "b", "drain"));
        var moved = Assert.Single(c["b"].Lobbies.Lobbies);
        Assert.Equal(code, moved.Code);
        Assert.True(moved.IsHost("lh"));
        Assert.Equal(2, moved.Members.Count);
        Assert.InRange(c["b"].Host.Now - c["b"].Mm.Queue.Get("q")!.QueuedAt, waited, waited + 1);
        Assert.Equal("b", await c["b"].Fleet.LocateAsync(LobbyModule<LineSim, Walk, Player>.LobbyUnit, code));

        // A newcomer on the draining server is sent on too; the room plays on to its end.
        c.Connect("late", a);
        await c.Settle();
        Assert.Contains(c.Moves, m => m == ("late", "a", "b", "drain"));
        Assert.Same(room, a.Host.Rooms.Single());
        Assert.False(drain.IsCompleted);
        await c.Run(5);
        Assert.Empty(a.Host.Rooms);
        Assert.True(await drain.WaitAsync(TimeSpan.FromSeconds(5)));
        // Its players went back to the menu and were told the server is going away.
        Assert.Equal(new[] { "menu:duel", "draining:" }, c.To("r1").TakeLast(2));
    }

    [Fact]
    public async Task A_lobby_playing_a_room_moves_once_the_room_is_over()
    {
        var c = await TwoServers(endAfter: 120);
        var a = c["a"];
        var lh = c.Connect("lh", a);
        a.Lobbies.Join(lh, "");
        var code = a.Lobbies.LobbyOf(lh)!.Code;
        a.Lobbies.Join(c.Connect("lg", a), code);
        a.Lobbies.Start(lh, "duo");
        Assert.Single(a.Host.Rooms);
        _ = a.Node.DrainAsync();
        await c.Beat();
        await c.Run(0.5);
        Assert.Empty(c.Moves); // still playing
        await c.Run(3);
        Assert.Contains(c.Moves, m => m == ("lh", "a", "b", "drain"));
        Assert.Equal(code, c["b"].Lobbies.Lobbies.Single().Code);
    }

    [Fact]
    public async Task Joining_or_opening_a_lobby_on_a_draining_server_goes_elsewhere()
    {
        var c = await TwoServers();
        var bHost = c.Connect("bh", c["b"]);
        c["b"].Lobbies.Join(bHost, "");
        var code = c["b"].Lobbies.LobbyOf(bHost)!.Code;
        _ = c["a"].Node.DrainAsync();
        await c.Beat();
        await c.Run(0.1);

        var g = c.Connect("g", c["a"]);
        await c.Settle();
        Assert.Contains(c.Moves, m => m == ("g", "a", "b", "drain"));
        c.Clients["g"].Server.Lobbies.Join(c.SessionOf("g"), code);
        Assert.Equal(2, c["b"].Lobbies.Lobbies.Single().Members.Count);

        // Asking a draining server for a new lobby: opened on b.
        var n = c.Connect("n", c["a"]);
        c["a"].Lobbies.Join(n, ""); // races its own arrival lookup: the lobby request wins
        await c.Settle();
        Assert.Equal(2, c["b"].Lobbies.Lobbies.Count);
    }

    [Fact]
    public async Task A_draining_server_without_anywhere_to_go_tells_its_players()
    {
        var c = new Cluster();
        var a = c.Add("a");
        await c.Beat();
        c.Connect("q", a);
        c.Queue("q");
        _ = a.Node.DrainAsync();
        await c.Run(0.1);
        Assert.Empty(c.Moves);
        Assert.Equal(new[] { "queue:Idle:duel:0/0", "draining:" }, c.To("q").TakeLast(2));
    }

    // ------------------------------------------------------------------ handover failures

    [Fact]
    public async Task A_redirect_to_an_unknown_server_is_refused()
    {
        var c = await TwoServers();
        var s = c.Connect("p", c["a"]);
        Assert.False(c["a"].Host.Redirect(s, "nowhere", "test"));
        Assert.False(c["a"].Host.Redirect(s, "a", "test")); // itself
        Assert.True(c["a"].Host.Redirect(s, "b", "test"));
        Assert.True(RoomHost<LineSim, Walk, Player>.IsLeaving(s));
    }

    [Fact]
    public async Task Work_handed_to_a_connection_that_left_meanwhile_waits_for_its_next_one()
    {
        var c = await TwoServers();
        await c["a"].Fleet.PutHandoffAsync("b", "p", new FleetHandoff("queue", new() { ["playlist"] = "duel", ["waited"] = "3" }, "a"));
        c.Connect("p", c["b"]);
        c.Disconnect("p"); // gone before the lookup came back
        await c.Settle();
        Assert.NotNull(await c["b"].Fleet.TakeHandoffAsync("p"));
    }

    [Fact]
    public async Task A_handoff_of_an_unknown_kind_or_playlist_is_ignored()
    {
        var c = await TwoServers();
        await c["a"].Fleet.PutHandoffAsync("b", "p", new FleetHandoff("queue", new() { ["playlist"] = "nope" }, "a"));
        c.Connect("p", c["b"]);
        await c.Settle();
        Assert.Null(c["b"].Mm.Queue.Get("p"));
        await c["a"].Fleet.PutHandoffAsync("b", "q", new FleetHandoff("something-else", new(), "a"));
        c.Connect("q", c["b"]);
        await c.Settle();
        Assert.Empty(c.To("q"));
    }
}
