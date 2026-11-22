using Altruist.Gaming.Matchmaking;
using Altruist.Gaming.Rooms;

namespace Tests.Gaming.Rooms;

/// <summary>
/// Forming rooms (RoomRules.FormBeforeStart): a room opens for whoever waits, bots arrive one by
/// one while nobody new comes, newcomers take open seats or bots' seats, and the match starts once
/// every seat is driven and nobody arrived for a while (at once when they are all players).
/// </summary>
public class FormingRoomTests
{
    /// <summary>Three cars, everyone against everyone (one team).</summary>
    private static readonly PlaylistOptions Race3 = new()
    {
        Id = "race-3",
        Mode = "race",
        TeamSize = 3,
        Teams = 1,
        Rules = new RoomRules
        {
            OnDisconnect = DisconnectAction.BotAfterDelay,
            OnVoluntaryLeave = VoluntaryLeaveAction.OpenForBot,
            JoinInProgress = true,
            FormBeforeStart = true,
        },
    };

    private static string Who(Seat<LineSim, Walk, Player> s) => s.Owner?.PrincipalId ?? (s.IsBot ? "bot" : "open");

    [Fact]
    public void A_queued_player_gets_a_forming_room_at_once()
    {
        var h = new RoomHostTests.Harness(30, Race3);
        h.Connect("a");
        h.Queue("a", Race3);
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        Assert.True(room.Forming);
        Assert.Equal(["a", "open", "open"], room.Seats.Select(Who));
        Assert.All(room.Seats, s => Assert.Equal(0, s.Team));
        Assert.Contains("welcome:1", h.Game.To("c-a"));
    }

    [Fact]
    public void Bots_arrive_one_by_one_then_the_match_starts()
    {
        var h = new RoomHostTests.Harness(30, Race3);
        h.Connect("a");
        h.Queue("a", Race3);
        h.Run(3.1);
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(["a", "bot", "open"], room.Seats.Select(Who));
        h.Run(3);
        Assert.Equal(["a", "bot", "bot"], room.Seats.Select(Who));
        Assert.True(room.Forming);
        h.Run(3.1);
        Assert.False(room.Forming);
        Assert.Equal([room.Id], h.Game.Formed);
    }

    [Fact]
    public void A_newcomer_takes_an_open_seat_and_holds_off_the_next_bot()
    {
        var h = new RoomHostTests.Harness(30, Race3);
        h.Connect("a");
        h.Connect("b");
        h.Queue("a", Race3);
        h.Run(2);
        h.Queue("b", Race3);
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(["a", "b", "open"], room.Seats.Select(Who));
        // The arrival restarts the wait: no bot 2 s later, one after the full wait.
        h.Run(2);
        Assert.Equal(["a", "b", "open"], room.Seats.Select(Who));
        h.Run(1.1);
        Assert.Equal(["a", "b", "bot"], room.Seats.Select(Who));
    }

    [Fact]
    public void A_newcomer_replaces_a_bot_before_the_start()
    {
        var h = new RoomHostTests.Harness(30, Race3);
        h.Connect("a");
        h.Connect("b");
        h.Queue("a", Race3);
        h.Run(6.1);
        var room = Assert.Single(h.Host.Rooms);
        Assert.Equal(["a", "bot", "bot"], room.Seats.Select(Who));
        h.Queue("b", Race3);
        h.Run(2.5);
        Assert.Contains("b", room.Seats.Select(Who));
        Assert.True(room.Forming);
        h.Run(3.1);
        Assert.False(room.Forming);
    }

    [Fact]
    public void A_full_room_of_players_starts_at_once()
    {
        var h = new RoomHostTests.Harness(30, Race3);
        foreach (var who in new[] { "a", "b", "c" })
        {
            h.Connect(who);
            h.Queue(who, Race3);
        }
        h.Run(0.05);
        var room = Assert.Single(h.Host.Rooms);
        Assert.False(room.Forming);
        Assert.Equal([room.Id], h.Game.Formed);
    }
}
