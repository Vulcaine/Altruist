/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Flow;

/// <summary>The operation of one <see cref="ModifierStack{TCtx}"/> entry.</summary>
public enum ModifierOp : byte
{
    /// <summary><c>value = operand</c></summary>
    Set,
    /// <summary><c>value = value + operand</c></summary>
    Add,
    /// <summary><c>value = value * operand</c></summary>
    Mul,
    /// <summary><c>value = MathF.Min(value, operand)</c></summary>
    Min,
    /// <summary><c>value = MathF.Max(value, operand)</c></summary>
    Max,
    /// <summary><c>value = map(context, value)</c></summary>
    Map,
}

/// <summary>
/// An ordered list of float modifiers (Set / Add / Mul / Min / Max / Map), each optionally
/// conditional, applied in order to a value: the replacement for a block of
/// <c>if (a) scale = x; else if (b) scale = y; if (c) scale = MathF.Min(scale, z); if (d) scale *= w;</c>.
///
/// <para>Float exactness: each entry is one float32 operation applied as written
/// (<c>value + x</c>, <c>value * x</c>, <c>MathF.Min(value, x)</c>, <c>MathF.Max(value, x)</c>) in
/// list order — no reassociation, no folding of constants, no reordering, so the result is
/// bit-identical to the inline code it replaces when that code applies the same operations in the
/// same order. The operand is computed (its delegate runs) only when the entry applies, exactly like
/// an expression inside an <c>if</c>.</para>
///
/// <para>Conditions: <c>when</c> (optional) gates an entry. <see cref="Builder.Else"/> makes the
/// next entry the <c>else</c> branch of the previous one: it is considered only when no entry of
/// its chain applied (an <c>if / else if / else if</c> chain is one entry followed by
/// <c>Else()</c> entries).</para>
///
/// <para>Allocation: none per call (an array of structs built once).</para>
///
/// <code>
/// var gravity = ModifierStack&lt;Vehicle&gt;.Create()
///     .Set("dash",       c =&gt; c.Config.DashGravity,  when: v =&gt; v.Dashing)
///     .Else().Set("stall", c =&gt; c.Config.StallGravity, when: v =&gt; v.Stalling)
///     .Min("afterglow",  v =&gt; v.AfterglowScale, when: v =&gt; v.InAfterglow)
///     .Mul("wall",       v =&gt; v.WallFactor,     when: v =&gt; v.OnSteep)
///     .Build();
/// v.GravityScale = gravity.Apply(1f, v);
/// </code>
/// </summary>
public sealed class ModifierStack<TCtx>
{
    private readonly string[] _names;
    private readonly Entry[] _entries;

    /// <summary>One modifier (opaque; built by <see cref="Builder"/>).</summary>
    public readonly struct Entry
    {
        internal readonly ModifierOp Op;
        internal readonly float Constant;
        internal readonly Func<TCtx, float>? Operand;
        internal readonly Func<TCtx, float, float>? Map;
        internal readonly Func<TCtx, bool>? When;
        internal readonly bool IsElse;

        internal Entry(ModifierOp op, float constant, Func<TCtx, float>? operand, Func<TCtx, float, float>? map, Func<TCtx, bool>? when, bool isElse)
        {
            Op = op;
            Constant = constant;
            Operand = operand;
            Map = map;
            When = when;
            IsElse = isElse;
        }
    }

    private ModifierStack(string[] names, Entry[] entries)
    {
        _names = names;
        _entries = entries;
    }

    /// <summary>Starts a new modifier list.</summary>
    public static Builder Create() => new();

    /// <summary>Number of modifiers.</summary>
    public int Count => _entries.Length;

    /// <summary>The name of the modifier at <paramref name="index"/> (application order).</summary>
    public string NameOf(int index) => _names[index];

    /// <summary>The operation of the modifier at <paramref name="index"/>.</summary>
    public ModifierOp OpOf(int index) => _entries[index].Op;

    /// <summary>Applies every modifier in order to <paramref name="value"/>.</summary>
    public float Apply(float value, TCtx context)
    {
        var entries = _entries;
        // Whether an entry of the current if / else-if chain applied.
        var chainTaken = false;
        for (var i = 0; i < entries.Length; i++)
        {
            ref readonly var e = ref entries[i];
            if (e.IsElse && chainTaken) continue;
            if (e.When is not null && !e.When(context))
            {
                if (!e.IsElse) chainTaken = false;
                continue;
            }
            chainTaken = true;
            switch (e.Op)
            {
                case ModifierOp.Set:
                    value = e.Operand is not null ? e.Operand(context) : e.Constant;
                    break;
                case ModifierOp.Add:
                    value = value + (e.Operand is not null ? e.Operand(context) : e.Constant);
                    break;
                case ModifierOp.Mul:
                    value = value * (e.Operand is not null ? e.Operand(context) : e.Constant);
                    break;
                case ModifierOp.Min:
                    value = MathF.Min(value, e.Operand is not null ? e.Operand(context) : e.Constant);
                    break;
                case ModifierOp.Max:
                    value = MathF.Max(value, e.Operand is not null ? e.Operand(context) : e.Constant);
                    break;
                default:
                    value = e.Map!(context, value);
                    break;
            }
        }
        return value;
    }

    /// <summary>Builds a <see cref="ModifierStack{TCtx}"/>; modifiers keep the order they are added in.</summary>
    public sealed class Builder : OrderedBuilder<Builder, Entry>
    {
        private bool _else;

        internal Builder() { }

        /// <summary>The next modifier is the <c>else</c> branch of the previous one: it is
        /// considered only when no modifier of that if / else-if chain applied.</summary>
        public Builder Else()
        {
            if (Names.Count == 0) throw new InvalidOperationException("Else() needs a previous modifier.");
            _else = true;
            return this;
        }

        /// <summary><c>value = operand</c>, the operand a constant; <paramref name="when"/> gates it.</summary>
        public Builder Set(string name, float value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Set, value, null, when);
        /// <summary><c>value = operand</c>, the operand computed from the context (only when the modifier applies); <paramref name="when"/> gates it.</summary>
        public Builder Set(string name, Func<TCtx, float> value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Set, 0f, value, when);
        /// <summary><c>value = value + operand</c>, the operand a constant; <paramref name="when"/> gates it.</summary>
        public Builder Add(string name, float value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Add, value, null, when);
        /// <summary><c>value = value + operand</c>, the operand computed from the context (only when the modifier applies); <paramref name="when"/> gates it.</summary>
        public Builder Add(string name, Func<TCtx, float> value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Add, 0f, value, when);
        /// <summary><c>value = value * operand</c>, the operand a constant; <paramref name="when"/> gates it.</summary>
        public Builder Mul(string name, float value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Mul, value, null, when);
        /// <summary><c>value = value * operand</c>, the operand computed from the context (only when the modifier applies); <paramref name="when"/> gates it.</summary>
        public Builder Mul(string name, Func<TCtx, float> value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Mul, 0f, value, when);
        /// <summary><c>value = MathF.Min(value, operand)</c>, the operand a constant; <paramref name="when"/> gates it.</summary>
        public Builder Min(string name, float value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Min, value, null, when);
        /// <summary><c>value = MathF.Min(value, operand)</c>, the operand computed from the context (only when the modifier applies); <paramref name="when"/> gates it.</summary>
        public Builder Min(string name, Func<TCtx, float> value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Min, 0f, value, when);
        /// <summary><c>value = MathF.Max(value, operand)</c>, the operand a constant; <paramref name="when"/> gates it.</summary>
        public Builder Max(string name, float value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Max, value, null, when);
        /// <summary><c>value = MathF.Max(value, operand)</c>, the operand computed from the context (only when the modifier applies); <paramref name="when"/> gates it.</summary>
        public Builder Max(string name, Func<TCtx, float> value, Func<TCtx, bool>? when = null) => Push(name, ModifierOp.Max, 0f, value, when);

        /// <summary>Any other operation: <c>value = map(context, value)</c>.</summary>
        public Builder Map(string name, Func<TCtx, float, float> map, Func<TCtx, bool>? when = null)
        {
            ArgumentNullException.ThrowIfNull(map);
            var isElse = _else;
            _else = false;
            return Put(name, new Entry(ModifierOp.Map, 0f, null, map, when, isElse));
        }

        private Builder Push(string name, ModifierOp op, float constant, Func<TCtx, float>? operand, Func<TCtx, bool>? when)
        {
            var isElse = _else;
            _else = false;
            return Put(name, new Entry(op, constant, operand, null, when, isElse));
        }

        /// <summary>An immutable modifier list in its current order.</summary>
        public ModifierStack<TCtx> Build()
        {
            if (_else) throw new InvalidOperationException("Else() was set but no modifier followed it.");
            var (names, entries) = Snapshot();
            return new ModifierStack<TCtx>(names, entries);
        }
    }
}
