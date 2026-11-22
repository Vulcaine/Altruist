/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Flow;

/// <summary>
/// Base of the flow builders (<see cref="FirstMatch{TCtx, TResult}"/>, <see cref="TickPipeline{TCtx}"/>,
/// <see cref="ModifierStack{TCtx}"/>, ...): an ordered list of uniquely named entries that can be
/// edited at setup time, so a game (or a mode, a mutator, a test) can insert, replace or remove a
/// rule or a step by name without copying the whole list.
///
/// <para>Every add appends, unless a one-shot position was set just before it:
/// <see cref="Before"/> / <see cref="After"/> insert the next entry next to an existing one,
/// <see cref="Replacing"/> puts the next entry in the place of an existing one. Names are unique
/// (case-sensitive); adding a duplicate throws. The built object keeps the final order, which is
/// the evaluation order: it never changes after <c>Build()</c>.</para>
/// </summary>
public abstract class OrderedBuilder<TSelf, TEntry> where TSelf : OrderedBuilder<TSelf, TEntry>
{
    private readonly List<string> _names = new();
    private readonly List<TEntry> _entries = new();
    private PendingPosition _pending;
    private string? _anchor;

    private enum PendingPosition : byte { None, Before, After, Replace }

    /// <summary>The entry names in their current order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>True when an entry with this name exists.</summary>
    public bool Contains(string name) => _names.IndexOf(name) >= 0;

    /// <summary>The next entry is inserted right before the entry named <paramref name="anchor"/>.</summary>
    public TSelf Before(string anchor) => SetPending(PendingPosition.Before, anchor);

    /// <summary>The next entry is inserted right after the entry named <paramref name="anchor"/>.</summary>
    public TSelf After(string anchor) => SetPending(PendingPosition.After, anchor);

    /// <summary>The next entry takes the place (position) of the entry named <paramref name="name"/>,
    /// which is removed. The new entry may keep the name or use another unique one.</summary>
    public TSelf Replacing(string name) => SetPending(PendingPosition.Replace, name);

    /// <summary>Removes the entry named <paramref name="name"/> (throws when there is none).</summary>
    public TSelf Remove(string name)
    {
        var i = IndexOrThrow(name);
        _names.RemoveAt(i);
        _entries.RemoveAt(i);
        return (TSelf)this;
    }

    /// <summary>Adds an entry at the end, or where the pending position says.</summary>
    protected TSelf Put(string name, TEntry entry)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Entries need a name.", nameof(name));
        var pending = _pending;
        var anchor = _anchor;
        _pending = PendingPosition.None;
        _anchor = null;

        int at;
        switch (pending)
        {
            case PendingPosition.Before:
                at = IndexOrThrow(anchor!);
                break;
            case PendingPosition.After:
                at = IndexOrThrow(anchor!) + 1;
                break;
            case PendingPosition.Replace:
                at = IndexOrThrow(anchor!);
                _names.RemoveAt(at);
                _entries.RemoveAt(at);
                break;
            default:
                at = _names.Count;
                break;
        }
        if (_names.IndexOf(name) >= 0)
            throw new InvalidOperationException($"An entry named '{name}' already exists.");
        _names.Insert(at, name);
        _entries.Insert(at, entry);
        return (TSelf)this;
    }

    /// <summary>The entries in order (copied); throws if a position was set but no entry followed.</summary>
    protected (string[] Names, TEntry[] Entries) Snapshot()
    {
        if (_pending != PendingPosition.None)
            throw new InvalidOperationException($"A position ({_pending} '{_anchor}') was set but no entry followed it.");
        return (_names.ToArray(), _entries.ToArray());
    }

    private TSelf SetPending(PendingPosition position, string anchor)
    {
        if (_pending != PendingPosition.None)
            throw new InvalidOperationException($"A position ({_pending} '{_anchor}') is already pending.");
        IndexOrThrow(anchor);
        _pending = position;
        _anchor = anchor;
        return (TSelf)this;
    }

    private int IndexOrThrow(string name)
    {
        var i = _names.IndexOf(name);
        if (i < 0) throw new KeyNotFoundException($"No entry named '{name}'.");
        return i;
    }
}

/// <summary>
/// The name of a flow entry added without one: its code (the <c>[CallerArgumentExpression]</c> of
/// the delegate that identifies it), lambda parameters dropped and whitespace collapsed, so
/// <c>.Step(s =&gt; s.AdvanceClock())</c> is named <c>s.AdvanceClock()</c>. Unique as long as the code
/// is; editing by name (Before / After / Replacing / Remove) takes the same text.
/// </summary>
public static class EntryName
{
    public static string Of(string code)
    {
        var arrow = code.IndexOf("=>", StringComparison.Ordinal);
        var body = arrow >= 0 && IsLambdaHeader(code.AsSpan(0, arrow)) ? code[(arrow + 2)..] : code;
        return string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsLambdaHeader(ReadOnlySpan<char> head)
    {
        foreach (var c in head)
            if (!(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c is '_' or ',' or '(' or ')'))
                return false;
        return true;
    }
}
