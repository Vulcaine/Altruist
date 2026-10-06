/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.Rooms;

namespace Altruist.Gaming.Matchmaking;

/// <summary>A queue players can join.</summary>
public sealed record PlaylistOptions
{
    public required string Id { get; init; }
    /// <summary>The room mode it starts (passed to the game's simulation).</summary>
    public required string Mode { get; init; }
    /// <summary>Seats per team; a match has two teams (0 and 1).</summary>
    public int TeamSize { get; init; } = 1;
    /// <summary>
    /// Rated: groups only form from players inside each other's rating window, teams are balanced
    /// by rating sum, and nobody is filled by bots. Unrated: a full group forms at once, otherwise
    /// whoever waits once the oldest waited <see cref="MatchmakingOptions.FillAfterSeconds"/> (bots fill the rest).
    /// </summary>
    public bool Rated { get; init; }
    /// <summary>The lifecycle rules of the rooms it starts (reconnects, leaves, join in progress, bots).</summary>
    public RoomRules Rules { get; init; } = new();

    public int PlayersNeeded => TeamSize * 2;
}

/// <summary>Timings and windows of matchmaking (passed by the game; not read from configuration).</summary>
public sealed record MatchmakingOptions
{
    /// <summary>Unrated: seconds the oldest player waits for others before bots fill.</summary>
    public double FillAfterSeconds { get; init; } = 6;
    /// <summary>Join in progress only into rooms with at least this much time left.</summary>
    public double JoinMinTimeLeftSeconds { get; init; } = 45;
    /// <summary>Minimum seconds between two bot swaps in one room.</summary>
    public double SwapIntervalSeconds { get; init; } = 4;
    /// <summary>Seconds between the "joining" notice and the swap.</summary>
    public double SwapNoticeSeconds { get; init; } = 2;
    public int RatingWindowStart { get; init; } = 100;
    public int RatingWindowGrowthPerSecond { get; init; } = 25;
    public int RatingWindowMax { get; init; } = 500;
    /// <summary>Seconds between "searching" status updates.</summary>
    public double StatusIntervalSeconds { get; init; } = 1;
    /// <summary>Fleet: seconds a player that just arrived from another server is not moved again.</summary>
    public double FleetMoveCooldownSeconds { get; init; } = 3;
}

public sealed record QueueEntry(string PrincipalId, string Playlist, int Rating, double QueuedAt);

/// <summary>
/// Queues per playlist and the rules that turn waiting players into groups. Pure (no clock, no
/// networking): callers pass the current time.
/// </summary>
public sealed class Matchmaker
{
    private readonly MatchmakingOptions _options;
    private readonly Dictionary<string, PlaylistOptions> _playlists = new();
    private readonly Dictionary<string, List<QueueEntry>> _queues = new();
    private readonly Dictionary<string, QueueEntry> _byPrincipal = new();

    public Matchmaker(MatchmakingOptions options, IEnumerable<PlaylistOptions> playlists)
    {
        _options = options;
        foreach (var p in playlists)
        {
            _playlists[p.Id] = p;
            _queues[p.Id] = new List<QueueEntry>();
        }
    }

    public MatchmakingOptions Options => _options;
    public IEnumerable<PlaylistOptions> Playlists => _playlists.Values;
    public int Count => _byPrincipal.Count;

    public PlaylistOptions? Playlist(string? id) => id is not null ? _playlists.GetValueOrDefault(id) : null;

    /// <summary>Adds (or moves) the principal to the playlist queue, keeping FIFO order by queue time.</summary>
    public void Enqueue(QueueEntry entry)
    {
        Remove(entry.PrincipalId);
        var q = _queues[entry.Playlist];
        // FIFO even when an entry comes back with its original time (an aborted swap).
        var i = q.FindIndex(e => e.QueuedAt > entry.QueuedAt);
        if (i < 0) q.Add(entry);
        else q.Insert(i, entry);
        _byPrincipal[entry.PrincipalId] = entry;
    }

    public bool Remove(string principalId)
    {
        if (!_byPrincipal.Remove(principalId, out var e)) return false;
        _queues[e.Playlist].Remove(e);
        return true;
    }

    public QueueEntry? Get(string principalId) => _byPrincipal.GetValueOrDefault(principalId);

    public IReadOnlyList<QueueEntry> Waiting(string playlist) => _queues[playlist];

    /// <summary>The oldest waiting player of a playlist, removed from the queue.</summary>
    public QueueEntry? PopOldest(string playlist)
    {
        var q = _queues[playlist];
        if (q.Count == 0) return null;
        var e = q[0];
        Remove(e.PrincipalId);
        return e;
    }

    /// <summary>Rating window after waiting <paramref name="waited"/> seconds (start, growing per whole second, capped).</summary>
    public static int RatingWindow(double waited, MatchmakingOptions o) =>
        (int)Math.Min(o.RatingWindowMax, o.RatingWindowStart + o.RatingWindowGrowthPerSecond * Math.Max(0, Math.Floor(waited)));

    /// <summary>
    /// Unrated: a full group as soon as enough players wait; otherwise, once the oldest waited
    /// <see cref="MatchmakingOptions.FillAfterSeconds"/>, whoever waits. <paramref name="reserved"/>
    /// players at the front are left for join in progress.
    /// </summary>
    public List<QueueEntry>? TryFormFill(string playlist, double now, int reserved = 0)
    {
        var q = _queues[playlist];
        var free = q.Skip(reserved).ToList();
        if (free.Count == 0) return null;
        var needed = _playlists[playlist].PlayersNeeded;
        List<QueueEntry> group;
        if (free.Count >= needed) group = free.Take(needed).ToList();
        else if (now - free[0].QueuedAt >= _options.FillAfterSeconds) group = free;
        else return null;
        foreach (var e in group) Remove(e.PrincipalId);
        return group;
    }

    /// <summary>
    /// Rated: the oldest player whose rating window (and every partner's) covers a full group;
    /// null until enough compatible players wait.
    /// </summary>
    public List<QueueEntry>? TryFormRated(string playlist, double now)
    {
        var q = _queues[playlist];
        var needed = _playlists[playlist].PlayersNeeded;
        if (q.Count < needed) return null;
        foreach (var anchor in q)
        {
            var group = new List<QueueEntry> { anchor };
            foreach (var c in q.Where(c => c != anchor).OrderBy(c => Math.Abs(c.Rating - anchor.Rating)).ThenBy(c => c.QueuedAt))
            {
                if (group.Count == needed) break;
                if (Compatible(group, c, now)) group.Add(c);
            }
            if (group.Count < needed) continue;
            foreach (var e in group) Remove(e.PrincipalId);
            return group;
        }
        return null;
    }

    /// <summary>Every pair in the group must sit inside both players' windows.</summary>
    private bool Compatible(List<QueueEntry> group, QueueEntry c, double now)
    {
        var wc = RatingWindow(now - c.QueuedAt, _options);
        foreach (var g in group)
        {
            var window = Math.Min(wc, RatingWindow(now - g.QueuedAt, _options));
            if (Math.Abs(g.Rating - c.Rating) > window) return false;
        }
        return true;
    }

    /// <summary>
    /// Splits players into two teams. By rating: the split with the smallest rating-sum difference
    /// (equal sizes). Otherwise players alternate, so team sizes differ by at most one.
    /// </summary>
    public static (List<QueueEntry> Team0, List<QueueEntry> Team1) BalanceTeams(IReadOnlyList<QueueEntry> players, bool byRating)
    {
        if (!byRating || players.Count < 2)
        {
            var a = new List<QueueEntry>();
            var b = new List<QueueEntry>();
            for (var i = 0; i < players.Count; i++) (i % 2 == 0 ? a : b).Add(players[i]);
            return (a, b);
        }
        var n = players.Count;
        var half = n / 2;
        var best = 0;
        var bestDiff = long.MaxValue;
        // Subsets of size n/2 that contain player 0 (the mirrored split is the same match).
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
}
