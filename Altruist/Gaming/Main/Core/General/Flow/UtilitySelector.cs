/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.Flow;

/// <summary>Read access to the scores of the decision in progress (by option or by index).</summary>
public readonly struct UtilityScores<TOption> where TOption : notnull
{
    private readonly float[] _scores;
    private readonly TOption[] _options;

    internal UtilityScores(float[] scores, TOption[] options)
    {
        _scores = scores;
        _options = options;
    }

    /// <summary>Number of options.</summary>
    public int Count => _scores.Length;

    /// <summary>The score of the option at <paramref name="index"/> (option order).</summary>
    public float this[int index] => _scores[index];

    /// <summary>The score of <paramref name="option"/>.</summary>
    public float this[TOption option] => _scores[UtilityOptions.IndexOf(_options, option)];
}

/// <summary>An override rule: sees the context, the current best option and the scores, and
/// returns the option to keep (the same <paramref name="best"/> to leave it unchanged).</summary>
public delegate TOption UtilityOverride<TOption, in TCtx>(TCtx context, TOption best, UtilityScores<TOption> scores)
    where TOption : notnull;

internal static class UtilityOptions
{
    public static int IndexOf<TOption>(TOption[] options, TOption option)
    {
        var cmp = EqualityComparer<TOption>.Default;
        for (var i = 0; i < options.Length; i++)
            if (cmp.Equals(options[i], option)) return i;
        throw new KeyNotFoundException($"'{option}' is not an option of this selector.");
    }
}

/// <summary>
/// Utility-based choice between a fixed set of options (roles, tactics, targets): the replacement
/// for a hand-written "score every role, add noise and hysteresis, take the best, then apply the
/// exceptions" block. One <see cref="Decide"/> runs these stages, always in this order:
/// <list type="number">
/// <item><b>Forced rules</b> (in order): the first whose predicate is true decides at once
/// (no scoring, no RNG draws). E.g. "kickoff until someone touched the ball".</item>
/// <item><b>Prepare</b> (optional): computes what the scorers share (once per decision).</item>
/// <item><b>Scores</b>, in option order: each option's scorer, then its veto (true sets the
/// score to <c>-∞</c>, so no noise or bonus can pick it). The veto sees the raw score.</item>
/// <item><b>Noise</b>, in option order, for every option marked noisy — vetoed ones included, so the
/// number of RNG draws per decision never depends on the situation: <c>score += draw(rng) * scale</c>
/// (default draw: <see cref="DeterministicRandom.Gaussian"/>). The scale is read once per decision.</item>
/// <item><b>Inertia</b>: the current option (unless transient) gets <c>heldBonus</c> while it has
/// been held for less than the hold time, <c>bonus</c> after: <c>held = (tick - since) * tickSeconds &lt; hold</c>.</item>
/// <item><b>Best</b>: starts from the current option (or the fallback when the current one is
/// transient), then walks the options in order and takes any with a strictly greater score (ties
/// keep the earlier pick).</item>
/// <item><b>Overrides</b> (in order): each may replace the best (they all run, each sees the
/// previous one's answer).</item>
/// </list>
/// <see cref="Decide"/> does not change the current option: call <see cref="Choose"/> (or use
/// <see cref="Select"/>, which does both) so the game can run its own "role changed" logic.
///
/// <para>Determinism: fixed option order everywhere, a fixed number of RNG draws per scored
/// decision, float32 arithmetic applied in the order above (<c>score + noise</c>,
/// <c>score + bonus</c>), strict comparisons. Allocation: none per decision.</para>
///
/// <para>Choosing: use this when several options compete on a numeric score and the choice should
/// be sticky (inertia) and optionally noisy (per-agent personality); use
/// <see cref="FirstMatch{TCtx, TResult}"/> when a fixed priority order of conditions decides; use
/// <see cref="AIStateMachine"/> / <see cref="StateMachine{TContext}"/> for the behaviour that runs
/// once an option is chosen (a common pattern: the selector picks a role, a state machine per role
/// runs it).</para>
///
/// <code>
/// var roles = UtilitySelector&lt;Role, BotCtx&gt;.Create()
///     .Force("kickoff", c =&gt; c.KickoffPending, Role.Kickoff)
///     .Option(Role.Attack, c =&gt; c.AttackScore)
///     .Option(Role.Defend, c =&gt; c.DefendScore, veto: (c, s) =&gt; c.NoGoalToDefend)
///     .Noise(c =&gt; c.Personality.RoleNoise)
///     .Inertia(c =&gt; 1.5f, heldBonus: 0.3f, bonus: 0.1f, tickSeconds: 1f / 60f)
///     .StartWith(Role.Defend)
///     .Build();
/// Role role = roles.Select(ctx, rng, tick, out bool changed);
/// </code>
/// </summary>
public sealed class UtilitySelector<TOption, TCtx> where TOption : notnull
{
    private readonly TOption[] _options;
    private readonly string[] _forcedNames;
    private readonly (Func<TCtx, bool> When, TOption Option)[] _forced;
    private readonly Action<TCtx>? _prepare;
    private readonly Func<TCtx, float>[] _scorers;
    private readonly Func<TCtx, float, bool>?[] _vetoes;
    private readonly bool[] _noisy;
    private readonly bool[] _transient;
    private readonly Func<TCtx, float>? _noiseScale;
    private readonly Func<DeterministicRandom, float> _draw;
    private readonly Func<TCtx, float>? _hold;
    private readonly float _heldBonus;
    private readonly float _bonus;
    private readonly float _tickSeconds;
    private readonly int _fallback;
    private readonly string[] _overrideNames;
    private readonly UtilityOverride<TOption, TCtx>[] _overrides;
    private readonly float[] _scores;

    private static readonly Func<DeterministicRandom, float> Gaussian = r => r.Gaussian();

    private UtilitySelector(Builder b)
    {
        var n = b.Options.Count;
        if (n == 0) throw new InvalidOperationException("A utility selector needs at least one option.");
        _options = new TOption[n];
        _scorers = new Func<TCtx, float>[n];
        _vetoes = new Func<TCtx, float, bool>?[n];
        _noisy = new bool[n];
        _transient = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var o = b.Options[i];
            _options[i] = o.Option;
            _scorers[i] = o.Score;
            _vetoes[i] = o.Veto;
            _noisy[i] = o.Noisy;
            _transient[i] = o.Transient;
        }
        _forcedNames = b.Forced.Select(f => f.Name).ToArray();
        _forced = b.Forced.Select(f => (f.When, f.Option)).ToArray();
        _prepare = b.PrepareHook;
        _noiseScale = b.NoiseScale;
        _draw = b.Draw ?? Gaussian;
        _hold = b.Hold;
        _heldBonus = b.HeldBonus;
        _bonus = b.Bonus;
        _tickSeconds = b.TickSeconds;
        _fallback = b.HasFallback ? UtilityOptions.IndexOf(_options, b.Fallback) : -1;
        _overrideNames = b.Overrides.Select(o => o.Name).ToArray();
        _overrides = b.Overrides.Select(o => o.Rule).ToArray();
        _scores = new float[n];
        CurrentIndex = b.HasInitial ? UtilityOptions.IndexOf(_options, b.Initial) : 0;
        PreviousIndex = CurrentIndex;
    }

    /// <summary>Starts a new selector.</summary>
    public static Builder Create() => new();

    /// <summary>The options in their fixed order.</summary>
    public IReadOnlyList<TOption> Options => _options;

    /// <summary>The current choice (see <see cref="Choose"/>).</summary>
    public TOption Current => _options[CurrentIndex];

    /// <summary>Index of the current choice.</summary>
    public int CurrentIndex { get; private set; }

    /// <summary>The choice before the current one.</summary>
    public TOption Previous => _options[PreviousIndex];

    /// <summary>Index of the choice before the current one.</summary>
    public int PreviousIndex { get; private set; }

    /// <summary>Tick at which the current choice was made.</summary>
    public int Since { get; private set; }

    /// <summary>The name of the forced rule that decided the last <see cref="Decide"/>, or null when it was scored.</summary>
    public string? LastForced { get; private set; }

    /// <summary>The scores of the last scored decision (option order).</summary>
    public UtilityScores<TOption> Scores => new(_scores, _options);

    /// <summary>Index of <paramref name="option"/> in the option order.</summary>
    public int IndexOf(TOption option) => UtilityOptions.IndexOf(_options, option);

    /// <summary>Runs the stages (see the class summary) and returns the best option. Does not
    /// change <see cref="Current"/>.</summary>
    /// <param name="context">Passed to every rule, scorer and veto.</param>
    /// <param name="rng">The noise source (drawn only when the decision is scored).</param>
    /// <param name="tick">The current tick (for the inertia hold time).</param>
    public TOption Decide(TCtx context, DeterministicRandom rng, int tick)
    {
        ArgumentNullException.ThrowIfNull(rng);
        for (var i = 0; i < _forced.Length; i++)
        {
            if (!_forced[i].When(context)) continue;
            LastForced = _forcedNames[i];
            return _forced[i].Option;
        }
        LastForced = null;
        _prepare?.Invoke(context);

        var u = _scores;
        var n = u.Length;
        for (var i = 0; i < n; i++)
        {
            var s = _scorers[i](context);
            if (_vetoes[i] is { } veto && veto(context, s)) s = float.NegativeInfinity;
            u[i] = s;
        }
        if (_noiseScale is not null)
        {
            var scale = _noiseScale(context);
            for (var i = 0; i < n; i++)
                if (_noisy[i]) u[i] += _draw(rng) * scale;
        }
        var cur = CurrentIndex;
        if (_hold is not null && !_transient[cur])
        {
            var held = (tick - Since) * _tickSeconds < _hold(context);
            u[cur] += held ? _heldBonus : _bonus;
        }
        var best = _transient[cur] && _fallback >= 0 ? _fallback : cur;
        for (var i = 0; i < n; i++)
            if (u[i] > u[best]) best = i;

        var pick = _options[best];
        if (_overrides.Length > 0)
        {
            var scores = Scores;
            for (var i = 0; i < _overrides.Length; i++) pick = _overrides[i](context, pick, scores);
        }
        return pick;
    }

    /// <summary>Makes <paramref name="option"/> the current choice at <paramref name="tick"/>.
    /// Returns false (and changes nothing, the hold time included) when it already is.</summary>
    public bool Choose(TOption option, int tick)
    {
        var i = UtilityOptions.IndexOf(_options, option);
        if (i == CurrentIndex) return false;
        PreviousIndex = CurrentIndex;
        CurrentIndex = i;
        Since = tick;
        return true;
    }

    /// <summary><see cref="Decide"/> then <see cref="Choose"/>; <paramref name="changed"/> tells
    /// whether the choice changed.</summary>
    public TOption Select(TCtx context, DeterministicRandom rng, int tick, out bool changed)
    {
        var pick = Decide(context, rng, tick);
        changed = Choose(pick, tick);
        return pick;
    }

    /// <summary>Restores the bookkeeping (rollback, replays, a reset).</summary>
    public void Restore(TOption current, TOption previous, int since)
    {
        CurrentIndex = UtilityOptions.IndexOf(_options, current);
        PreviousIndex = UtilityOptions.IndexOf(_options, previous);
        Since = since;
    }

    /// <summary>Builds a <see cref="UtilitySelector{TOption, TCtx}"/>.</summary>
    public sealed class Builder
    {
        internal readonly List<OptionDef> Options = new();
        internal readonly List<(string Name, Func<TCtx, bool> When, TOption Option)> Forced = new();
        internal readonly List<(string Name, UtilityOverride<TOption, TCtx> Rule)> Overrides = new();
        internal Action<TCtx>? PrepareHook;
        internal Func<TCtx, float>? NoiseScale;
        internal Func<DeterministicRandom, float>? Draw;
        internal Func<TCtx, float>? Hold;
        internal float HeldBonus;
        internal float Bonus;
        internal float TickSeconds;
        internal TOption Fallback = default!;
        internal bool HasFallback;
        internal TOption Initial = default!;
        internal bool HasInitial;

        internal sealed record OptionDef(TOption Option, Func<TCtx, float> Score, Func<TCtx, float, bool>? Veto, bool Noisy, bool Transient);

        internal Builder() { }

        /// <summary>Adds an option (the order of calls is the option order: scoring, noise draws and
        /// the best-pick walk all follow it).</summary>
        /// <param name="option">The option.</param>
        /// <param name="score">Its utility.</param>
        /// <param name="veto">True rules the option out (score <c>-∞</c>); sees the raw score.</param>
        /// <param name="noisy">Whether noise is added to it (default true).</param>
        /// <param name="transient">An option entered only by forced rules or the game (a kickoff):
        /// no inertia bonus, and while it is current the best-pick walk starts from the fallback.</param>
        public Builder Option(TOption option, Func<TCtx, float> score, Func<TCtx, float, bool>? veto = null, bool noisy = true, bool transient = false)
        {
            ArgumentNullException.ThrowIfNull(score);
            if (Options.Any(o => EqualityComparer<TOption>.Default.Equals(o.Option, option)))
                throw new InvalidOperationException($"Option '{option}' was added twice.");
            Options.Add(new OptionDef(option, score, veto, noisy, transient));
            return this;
        }

        /// <summary>Adds an option with a constant score.</summary>
        public Builder Option(TOption option, float score, Func<TCtx, float, bool>? veto = null, bool noisy = true, bool transient = false)
            => Option(option, _ => score, veto, noisy, transient);

        /// <summary>A forced rule (checked in order before any scoring).</summary>
        public Builder Force(string name, Func<TCtx, bool> when, TOption option)
        {
            ArgumentNullException.ThrowIfNull(when);
            Forced.Add((name, when, option));
            return this;
        }

        /// <summary>Runs once per scored decision, before the scorers (shared computations).</summary>
        public Builder Prepare(Action<TCtx> prepare)
        {
            PrepareHook = prepare ?? throw new ArgumentNullException(nameof(prepare));
            return this;
        }

        /// <summary>Noise on noisy options: <c>score += draw(rng) * scale(context)</c>; the default
        /// draw is <see cref="DeterministicRandom.Gaussian"/>.</summary>
        public Builder Noise(Func<TCtx, float> scale, Func<DeterministicRandom, float>? draw = null)
        {
            NoiseScale = scale ?? throw new ArgumentNullException(nameof(scale));
            Draw = draw;
            return this;
        }

        /// <summary>Hysteresis: the current option gets <paramref name="heldBonus"/> while
        /// <c>(tick - since) * tickSeconds &lt; hold(context)</c>, <paramref name="bonus"/> after.</summary>
        public Builder Inertia(Func<TCtx, float> hold, float heldBonus, float bonus, float tickSeconds)
        {
            Hold = hold ?? throw new ArgumentNullException(nameof(hold));
            HeldBonus = heldBonus;
            Bonus = bonus;
            TickSeconds = tickSeconds;
            return this;
        }

        /// <summary>Where the best-pick walk starts while the current option is transient.</summary>
        public Builder FallbackTo(TOption option)
        {
            Fallback = option;
            HasFallback = true;
            return this;
        }

        /// <summary>The current option before any choice (default: the first option).</summary>
        public Builder StartWith(TOption option)
        {
            Initial = option;
            HasInitial = true;
            return this;
        }

        /// <summary>An override rule, applied in order after the best pick.</summary>
        public Builder Override(string name, UtilityOverride<TOption, TCtx> rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            Overrides.Add((name, rule));
            return this;
        }

        /// <summary>A selector with the options and rules in their current order.</summary>
        public UtilitySelector<TOption, TCtx> Build() => new(this);
    }
}
