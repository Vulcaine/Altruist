/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Runtime.CompilerServices;

namespace Altruist.Gaming.Flow;

/// <summary>A rule that may decline after looking closer: returns true (with a result) when it
/// handles the context, false to let the next rule try.</summary>
public delegate bool TryRule<in TCtx, TResult>(TCtx context, out TResult result);

/// <summary>
/// Ordered rule selector: the replacement for an <c>if / else if</c> ladder that picks a result.
/// Rules are evaluated in the order they were built; the first one that matches decides, the
/// rest are not evaluated (their predicates never run). An optional default answers when none
/// matched.
///
/// <para>Rule kinds:</para>
/// <list type="bullet">
/// <item><c>When(name, predicate, then)</c> — the predicate decides; <c>then</c> produces the result.</item>
/// <item><c>When(name, predicate, value)</c> — a constant result.</item>
/// <item><c>Try(name, rule)</c> — the rule looks and may decline (returns false), e.g. a tactic
/// that rolls a chance or latches a flag before deciding; the next rule is tried then.</item>
/// </list>
///
/// <para>Determinism: the order is fixed at <see cref="Builder.Build"/>, evaluation is a plain
/// loop over an array, and nothing is reordered or cached between calls. Every predicate up to
/// the matching rule runs exactly once, in order (so predicates with side effects, such as RNG
/// draws, consume the same sequence as the inline ladder they replace).</para>
///
/// <para>Allocation: none per evaluation (the rules are an array of structs built once). Keep
/// the delegates free of per-call captures (pass state through the context) to stay that way.</para>
///
/// <para>Choosing: use this for a stateless "first rule that applies picks the value" decision
/// re-evaluated every call; <see cref="FirstMatch{TCtx}"/> when the branches run actions instead
/// of returning a value; <see cref="UtilitySelector{TOption, TCtx}"/> when options are scored and
/// the best one wins (with noise and hysteresis); <see cref="StateMachine{TContext}"/> /
/// <see cref="AIStateMachine"/> when the decision has memory (a current state that persists over
/// ticks, with enter/exit hooks and time in state); <see cref="ModifierStack{TCtx}"/> when several
/// conditional operations combine into one float instead of one rule winning.</para>
///
/// <code>
/// var hitMultiplier = FirstMatch&lt;HitContext, float&gt;.Create()
///     .When("super",   c =&gt; c.SuperStrike,   c =&gt; c.Config.SuperStrikeMultiplier)
///     .When("kick",    c =&gt; c.Kick,          c =&gt; MathF.Max(c.Base, c.Config.Nose) * c.Config.FlipKick)
///     .When("perfect", c =&gt; c.PerfectStrike, c =&gt; c.Base * c.Config.Perfect)
///     .When("dash",    c =&gt; c.DashStrike,    c =&gt; c.Base * c.Config.Dash)
///     .Otherwise(c =&gt; c.Base)
///     .Build();
/// var mult = hitMultiplier.Evaluate(ctx);
/// </code>
/// </summary>
public sealed class FirstMatch<TCtx, TResult>
{
    private readonly string[] _names;
    private readonly Rule[] _rules;
    private readonly Func<TCtx, TResult>? _otherwise;
    private readonly bool _hasOtherwise;
    private readonly TResult _otherwiseValue;

    /// <summary>One rule (opaque; built by <see cref="Builder"/>).</summary>
    public readonly struct Rule
    {
        internal readonly Func<TCtx, bool>? When;
        internal readonly Func<TCtx, TResult>? Then;
        internal readonly TResult Value;
        internal readonly TryRule<TCtx, TResult>? Try;

        internal Rule(Func<TCtx, bool>? when, Func<TCtx, TResult>? then, TResult value, TryRule<TCtx, TResult>? @try)
        {
            When = when;
            Then = then;
            Value = value;
            Try = @try;
        }
    }

    private FirstMatch(string[] names, Rule[] rules, bool hasOtherwise, Func<TCtx, TResult>? otherwise, TResult otherwiseValue)
    {
        _names = names;
        _rules = rules;
        _hasOtherwise = hasOtherwise;
        _otherwise = otherwise;
        _otherwiseValue = otherwiseValue;
    }

    /// <summary>Starts a new rule list.</summary>
    public static Builder Create() => new();

    /// <summary>Number of rules (the default not counted).</summary>
    public int Count => _rules.Length;

    /// <summary>True when a default answers if no rule matched.</summary>
    public bool HasDefault => _hasOtherwise;

    /// <summary>The name of the rule at <paramref name="index"/> (evaluation order).</summary>
    public string NameOf(int index) => _names[index];

    /// <summary>Index of the rule named <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name) => Array.IndexOf(_names, name);

    /// <summary>The first matching rule's result, else the default's. Throws
    /// <see cref="InvalidOperationException"/> when nothing matched and there is no default.</summary>
    public TResult Evaluate(TCtx context)
    {
        if (TryEvaluate(context, out var result, out _)) return result;
        throw new InvalidOperationException("No rule matched and there is no default.");
    }

    /// <summary>Evaluates the rules; false only when nothing matched and there is no default.</summary>
    public bool TryEvaluate(TCtx context, out TResult result) => TryEvaluate(context, out result, out _);

    /// <summary>Evaluates the rules. <paramref name="ruleIndex"/> is the matching rule's index, or
    /// -1 when the default answered (or nothing matched: then the method returns false).</summary>
    public bool TryEvaluate(TCtx context, out TResult result, out int ruleIndex)
    {
        var rules = _rules;
        for (var i = 0; i < rules.Length; i++)
        {
            ref readonly var r = ref rules[i];
            if (r.Try is not null)
            {
                if (r.Try(context, out result))
                {
                    ruleIndex = i;
                    return true;
                }
                continue;
            }
            if (!r.When!(context)) continue;
            result = r.Then is not null ? r.Then(context) : r.Value;
            ruleIndex = i;
            return true;
        }
        ruleIndex = -1;
        if (_hasOtherwise)
        {
            result = _otherwise is not null ? _otherwise(context) : _otherwiseValue;
            return true;
        }
        result = default!;
        return false;
    }

    /// <summary>Builds a <see cref="FirstMatch{TCtx, TResult}"/>; rules keep the order they are added in
    /// (see <see cref="OrderedBuilder{TSelf, TEntry}"/> for inserting or replacing by name).</summary>
    public sealed class Builder : OrderedBuilder<Builder, Rule>
    {
        private bool _hasOtherwise;
        private Func<TCtx, TResult>? _otherwise;
        private TResult _otherwiseValue = default!;

        internal Builder() { }

        /// <summary>A rule named by its condition's code (<see cref="EntryName"/>).</summary>
        public Builder When(Func<TCtx, bool> when, Func<TCtx, TResult> then, [CallerArgumentExpression(nameof(when))] string code = "") => When(EntryName.Of(code), when, then);

        /// <summary>A rule named by its condition's code (<see cref="EntryName"/>).</summary>
        public Builder When(Func<TCtx, bool> when, TResult value, [CallerArgumentExpression(nameof(when))] string code = "") => When(EntryName.Of(code), when, value);

        /// <summary>A rule named by its code (<see cref="EntryName"/>).</summary>
        public Builder Try(TryRule<TCtx, TResult> rule, [CallerArgumentExpression(nameof(rule))] string code = "") => Try(EntryName.Of(code), rule);

        /// <summary>When <paramref name="when"/> is true, <paramref name="then"/> gives the result.</summary>
        public Builder When(string name, Func<TCtx, bool> when, Func<TCtx, TResult> then)
        {
            ArgumentNullException.ThrowIfNull(when);
            ArgumentNullException.ThrowIfNull(then);
            return Put(name, new Rule(when, then, default!, null));
        }

        /// <summary>When <paramref name="when"/> is true, the result is <paramref name="value"/>.</summary>
        public Builder When(string name, Func<TCtx, bool> when, TResult value)
        {
            ArgumentNullException.ThrowIfNull(when);
            return Put(name, new Rule(when, null, value, null));
        }

        /// <summary>A rule that may decline (returns false) after looking closer.</summary>
        public Builder Try(string name, TryRule<TCtx, TResult> rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            return Put(name, new Rule(null, null, default!, rule));
        }

        /// <summary>The result when no rule matched.</summary>
        public Builder Otherwise(Func<TCtx, TResult> otherwise)
        {
            ArgumentNullException.ThrowIfNull(otherwise);
            _hasOtherwise = true;
            _otherwise = otherwise;
            return this;
        }

        /// <summary>The constant result when no rule matched.</summary>
        public Builder Otherwise(TResult value)
        {
            _hasOtherwise = true;
            _otherwise = null;
            _otherwiseValue = value;
            return this;
        }

        /// <summary>An immutable selector with the rules in their current order.</summary>
        public FirstMatch<TCtx, TResult> Build()
        {
            var (names, rules) = Snapshot();
            return new FirstMatch<TCtx, TResult>(names, rules, _hasOtherwise, _otherwise, _otherwiseValue);
        }
    }
}

/// <summary>
/// Ordered action selector: the replacement for an <c>if / else if</c> ladder whose branches DO
/// something (movement modes, input actions, tactics). The first rule that matches runs its action;
/// later rules are not evaluated. Same ordering, determinism and allocation guarantees as
/// <see cref="FirstMatch{TCtx, TResult}"/>.
///
/// <para>Choosing: prefer this over <see cref="FirstMatch{TCtx, TResult}"/> when each branch
/// performs work rather than producing a value, and over <see cref="TickPipeline{TCtx}"/> when only
/// ONE branch should run (a pipeline runs every step in order).</para>
///
/// <code>
/// var movement = FirstMatch&lt;MoveContext&gt;.Create()
///     .When("jump",      c =&gt; c.JumpPressed &amp;&amp; c.CanJump, Jump)
///     .When("flip",      c =&gt; c.JumpPressed &amp;&amp; c.AirJumpReady &amp;&amp; c.FlipDir != 0, Flip)
///     .When("air-jump",  c =&gt; c.JumpPressed &amp;&amp; c.AirJumpReady, AirJump)
///     .When("drive",     c =&gt; c.Grounded, Drive)
///     .Otherwise(AirControl)
///     .Build();
/// movement.Run(ctx);
/// </code>
/// </summary>
public sealed class FirstMatch<TCtx>
{
    private readonly string[] _names;
    private readonly Rule[] _rules;
    private readonly Action<TCtx>? _otherwise;

    /// <summary>One rule (opaque; built by <see cref="Builder"/>).</summary>
    public readonly struct Rule
    {
        internal readonly Func<TCtx, bool>? When;
        internal readonly Action<TCtx>? Then;
        internal readonly Func<TCtx, bool>? Try;

        internal Rule(Func<TCtx, bool>? when, Action<TCtx>? then, Func<TCtx, bool>? @try)
        {
            When = when;
            Then = then;
            Try = @try;
        }
    }

    private FirstMatch(string[] names, Rule[] rules, Action<TCtx>? otherwise)
    {
        _names = names;
        _rules = rules;
        _otherwise = otherwise;
    }

    /// <summary>Starts a new rule list.</summary>
    public static Builder Create() => new();

    /// <summary>Number of rules (the default not counted).</summary>
    public int Count => _rules.Length;

    /// <summary>The name of the rule at <paramref name="index"/> (evaluation order).</summary>
    public string NameOf(int index) => _names[index];

    /// <summary>Index of the rule named <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name) => Array.IndexOf(_names, name);

    /// <summary>Runs the first matching rule's action (or the default). Returns false only when
    /// nothing matched and there is no default.</summary>
    public bool Run(TCtx context) => Run(context, out _);

    /// <summary>Runs the first matching rule's action (or the default). <paramref name="ruleIndex"/>
    /// is the rule that ran, or -1 for the default / nothing.</summary>
    public bool Run(TCtx context, out int ruleIndex)
    {
        var rules = _rules;
        for (var i = 0; i < rules.Length; i++)
        {
            ref readonly var r = ref rules[i];
            if (r.Try is not null)
            {
                if (r.Try(context))
                {
                    ruleIndex = i;
                    return true;
                }
                continue;
            }
            if (!r.When!(context)) continue;
            r.Then!(context);
            ruleIndex = i;
            return true;
        }
        ruleIndex = -1;
        if (_otherwise is null) return false;
        _otherwise(context);
        return true;
    }

    /// <summary>Builds a <see cref="FirstMatch{TCtx}"/>; rules keep the order they are added in.</summary>
    public sealed class Builder : OrderedBuilder<Builder, Rule>
    {
        private Action<TCtx>? _otherwise;

        internal Builder() { }

        /// <summary>A rule named by its condition's code (<see cref="EntryName"/>).</summary>
        public Builder When(Func<TCtx, bool> when, Action<TCtx> then, [CallerArgumentExpression(nameof(when))] string code = "") => When(EntryName.Of(code), when, then);

        /// <summary>A rule named by its code (<see cref="EntryName"/>).</summary>
        public Builder Try(Func<TCtx, bool> rule, [CallerArgumentExpression(nameof(rule))] string code = "") => Try(EntryName.Of(code), rule);

        /// <summary>When <paramref name="when"/> is true, run <paramref name="then"/>.</summary>
        public Builder When(string name, Func<TCtx, bool> when, Action<TCtx> then)
        {
            ArgumentNullException.ThrowIfNull(when);
            ArgumentNullException.ThrowIfNull(then);
            return Put(name, new Rule(when, then, null));
        }

        /// <summary>A rule that acts and returns true, or declines (returns false) so the next rule tries.</summary>
        public Builder Try(string name, Func<TCtx, bool> rule)
        {
            ArgumentNullException.ThrowIfNull(rule);
            return Put(name, new Rule(null, null, rule));
        }

        /// <summary>The action when no rule matched.</summary>
        public Builder Otherwise(Action<TCtx> otherwise)
        {
            _otherwise = otherwise ?? throw new ArgumentNullException(nameof(otherwise));
            return this;
        }

        /// <summary>An immutable selector with the rules in their current order.</summary>
        public FirstMatch<TCtx> Build()
        {
            var (names, rules) = Snapshot();
            return new FirstMatch<TCtx>(names, rules, _otherwise);
        }
    }
}
