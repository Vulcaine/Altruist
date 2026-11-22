using Altruist;
using Altruist.Gaming.Lobbies;
using Altruist.Gaming.Matchmaking;
using Altruist.Gaming.Rooms;

namespace Tests.Gaming.Rooms;

public class RoomsRegressionTests
{
    // The previous implementation: every mask in ascending order (valid up to 30 players).
    private static (List<QueueEntry>, List<QueueEntry>) ReferenceBalance(IReadOnlyList<QueueEntry> players)
    {
        var n = players.Count;
        var half = n / 2;
        var best = 0;
        var bestDiff = long.MaxValue;
        for (var mask = 0; mask < 1 << n; mask++)
        {
            if ((mask & 1) == 0 || System.Numerics.BitOperations.PopCount((uint)mask) != half) continue;
            long diff = 0;
            for (var i = 0; i < n; i++) diff += (mask & (1 << i)) != 0 ? players[i].Rating : -players[i].Rating;
            diff = Math.Abs(diff);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = mask;
            }
        }
        var t0 = new List<QueueEntry>();
        var t1 = new List<QueueEntry>();
        for (var i = 0; i < n; i++) ((best & (1 << i)) != 0 ? t0 : t1).Add(players[i]);
        return (t0, t1);
    }

    [Fact]
    public void Rating_split_picks_the_same_teams_as_the_exhaustive_mask_scan()
    {
        var r = new Random(3);
        for (var round = 0; round < 400; round++)
        {
            var n = r.Next(2, 17);
            var players = Enumerable.Range(0, n)
                .Select(i => new QueueEntry($"p{i}", "ranked", 1000 + r.Next(0, 8) * 50, i))
                .ToList();

            var (a0, a1) = Matchmaker.BalanceTeams(players, byRating: true);
            var (e0, e1) = ReferenceBalance(players);

            Assert.Equal(e0, a0);
            Assert.Equal(e1, a1);
        }
    }

    [Fact]
    public void Rating_split_rejects_groups_whose_masks_would_overflow()
    {
        var players = Enumerable.Range(0, 31).Select(i => new QueueEntry($"p{i}", "ranked", 1000, i)).ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => Matchmaker.BalanceTeams(players, byRating: true));
    }

    [Fact]
    public void Lobby_handoff_into_a_full_lobby_is_not_taken()
    {
        var game = new LineGame();
        var host = new RoomHost<LineSim, Walk, Player>(game, game, new RoomHostOptions(), 60);
        var lobbies = new LobbyModule<LineSim, Walk, Player>(game, new LobbyOptions { MaxMembers = 1 });
        host.Use(lobbies);
        var owner = host.Connect("c-h", "h", new Player("h", "H", 1000))!;
        lobbies.Join(owner, "");
        var code = lobbies.LobbyOf(owner)!.Code;
        var guest = host.Connect("c-g", "g", new Player("g", "G", 1000))!;

        var taken = lobbies.OnHandoff(guest, new FleetHandoff(LobbyModule<LineSim, Walk, Player>.LobbyUnit,
            new Dictionary<string, string> { ["code"] = code }, "other"));

        Assert.False(taken);
        Assert.Null(lobbies.LobbyOf(guest));
    }
}
