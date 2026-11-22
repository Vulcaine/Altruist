/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>The plan a <see cref="RecedingPlanner{TCand, TFrame}"/> is executing.</summary>
public sealed class PlannedPlan<TCand, TFrame>
{
    public required TCand Cand;

    /// <summary>Per-tick inputs from <see cref="Start"/>.</summary>
    public required TFrame[] Frames;

    /// <summary>Tick of <c>Frames[0]</c>.</summary>
    public int Start;

    public float Score;

    /// <summary>Tick it was last scored.</summary>
    public int Checked;

    /// <summary>Score noise drawn for it: near-equal plans win unpredictably.</summary>
    public float Noise;

    /// <summary>Its first press was executed.</summary>
    public bool Committed;

    /// <summary>Tick of the first press.</summary>
    public int? CommitTick;
}

/// <summary>What the game supplies to <see cref="RecedingPlanner{TCand, TFrame}.Search"/>.</summary>
public interface IPlannerOps<TCand, TFrame>
{
    /// <summary>Tick the candidate's program starts (<c>Frames(c)[0]</c>).</summary>
    int Start(TCand c);

    /// <summary>The candidate's per-tick inputs from its start, long enough to be scored at <paramref name="tick"/>.</summary>
    TFrame[] Frames(TCand c, int tick);

    /// <summary>True when the frame presses a button (a press before now never happened: the candidate is stale).</summary>
    bool Pressed(TFrame f);

    /// <summary>Plays <paramref name="frames"/> from index <paramref name="offset"/> (the current tick) and scores the outcome.</summary>
    float Evaluate(TCand c, TFrame[] frames, int offset);

    /// <summary>One noise draw (scaled by the caller).</summary>
    float Noise();

    /// <summary>Neighbouring candidates for the refinement.</summary>
    IEnumerable<TCand> Neighbours(TCand c);

    /// <summary>A better plan replaced one whose first press already ran (it continues the same attempt).</summary>
    void OnReplan(PlannedPlan<TCand, TFrame> previous);
}

/// <summary>
/// An anytime receding-horizon planner (mirror of the TypeScript <c>RecedingPlanner</c> in
/// <c>@altruist/ai</c>). Candidates are parameterised input programs; each is played forward
/// by the game (a rollout on a scratch copy of the simulation) and scored, a few per tick within a
/// budget. The best one runs as exact per-tick inputs and is re-scored from the current tick every few
/// ticks; a better one replaces it. When the queue runs dry, the neighbours of the best candidate so
/// far are queued (a local refinement), a set number of rounds.
/// </summary>
/// <remarks>
/// <see cref="Search"/> runs, in this order: the running plan is re-scored (when due and the budget
/// allows, one unit) → queued candidates in order: frames → a candidate whose presses fell before now
/// is skipped (no unit) → evaluate (one unit) → best so far (strictly greater) → noise draw → it
/// replaces the plan when <c>score + noise &gt; plan.Score + plan.Noise + switchMargin</c> → once the
/// queue is empty, the best one's neighbours are queued (up to <c>refineRounds</c> times).
/// Deterministic: the only draws are the caller's <see cref="IPlannerOps{TCand, TFrame}.Noise"/>, one
/// per evaluated candidate.
/// </remarks>
public sealed class RecedingPlanner<TCand, TFrame>
{
    /// <summary>A re-scoring is due at once (the plan was just changed or slid).</summary>
    private const int Due = -1_000_000_000;

    private readonly int _recheckTicks;
    private readonly float _switchMargin;
    private readonly int _refineRounds;
    private Queue<TCand> _queue = new();

    /// <param name="recheckTicks">Ticks between re-scorings of the running plan.</param>
    /// <param name="switchMargin">Score (with noise) a new candidate must beat the running plan by.</param>
    /// <param name="refineRounds">Refinement rounds around the best candidate per restart.</param>
    public RecedingPlanner(int recheckTicks, float switchMargin, int refineRounds)
    {
        _recheckTicks = recheckTicks;
        _switchMargin = switchMargin;
        _refineRounds = refineRounds;
    }

    /// <summary>The plan being executed (null: none).</summary>
    public PlannedPlan<TCand, TFrame>? Plan { get; private set; }

    /// <summary>Best candidate of this pass (null before the first evaluation).</summary>
    public (TCand Cand, float Score)? Best { get; private set; }

    /// <summary>Tick of the last restart.</summary>
    public int EpochTick { get; private set; } = Due;

    /// <summary>Refinement rounds done this pass.</summary>
    public int Refined { get; private set; }

    /// <summary>Candidates still waiting to be evaluated.</summary>
    public int Pending => _queue.Count;

    /// <summary>A new pass: these candidates, no best yet, the running plan re-scored at once.</summary>
    public void Restart(int tick, IEnumerable<TCand> candidates)
    {
        EpochTick = tick;
        _queue = new Queue<TCand>(candidates);
        Best = null;
        Refined = 0;
        if (Plan is not null) Plan.Checked = Due;
    }

    /// <summary>Takes a plan found elsewhere (e.g. searched in full ahead of time) as a finished pass.</summary>
    public void Adopt(TCand cand, TFrame[] frames, int tick, float score)
    {
        Plan = new PlannedPlan<TCand, TFrame> { Cand = cand, Frames = frames, Start = tick, Score = score, Checked = tick, Committed = false, Noise = 0 };
        EpochTick = tick;
        _queue = new Queue<TCand>();
        Best = (cand, score);
        Refined = _refineRounds;
    }

    /// <summary>Spends up to <paramref name="budget"/> evaluations; returns the units left.</summary>
    public int Search(int tick, int budget, IPlannerOps<TCand, TFrame> ops)
    {
        var ep = Plan;
        if (ep is not null && budget > 0 && tick - ep.Checked >= _recheckTicks)
        {
            ep.Score = ops.Evaluate(ep.Cand, ep.Frames, tick - ep.Start);
            ep.Checked = tick;
            budget--;
        }
        while (budget > 0 && _queue.Count > 0)
        {
            var c = _queue.Dequeue();
            var all = ops.Frames(c, tick);
            var off = tick - ops.Start(c);
            var stale = false;
            for (var i = 0; i < off && !stale; i++)
                if (ops.Pressed(all[i])) stale = true;
            if (stale) continue;
            var score = ops.Evaluate(c, all, off);
            budget--;
            if (Best is not { } b || score > b.Score) Best = (c, score);
            var noise = ops.Noise();
            if (Plan is null || score + noise > Plan.Score + Plan.Noise + _switchMargin)
            {
                var prev = Plan;
                Plan = new PlannedPlan<TCand, TFrame> { Cand = c, Frames = all, Start = ops.Start(c), Score = score, Checked = tick, Committed = prev?.Committed ?? false, CommitTick = prev?.CommitTick, Noise = noise };
                if (prev is { Committed: true }) ops.OnReplan(prev);
            }
        }
        if (_queue.Count == 0 && Best is { } best && Refined < _refineRounds)
        {
            _queue = new Queue<TCand>(ops.Neighbours(best.Cand));
            Refined++;
        }
        return budget;
    }

    /// <summary>The search is done, the plan is a sure thing (<paramref name="sure"/>), or the pass is <paramref name="wait"/> ticks old.</summary>
    public bool Settled(int tick, float sure, int wait) =>
        (_queue.Count == 0 && Refined >= _refineRounds) || (Plan is not null && Plan.Score >= sure) || tick - EpochTick >= wait;

    /// <summary>Holds the plan back a tick (its inputs one tick later), re-scored next time.</summary>
    public void Slide()
    {
        if (Plan is null) return;
        Plan.Start++;
        Plan.Checked = Due;
    }

    /// <summary>Marks the plan's first press as executed; true the first time.</summary>
    public bool Commit(int tick)
    {
        if (Plan is null || Plan.Committed) return false;
        Plan.Committed = true;
        Plan.CommitTick = tick;
        return true;
    }

    /// <summary>Drops the plan; returns it.</summary>
    public PlannedPlan<TCand, TFrame>? Drop()
    {
        var p = Plan;
        Plan = null;
        return p;
    }
}
