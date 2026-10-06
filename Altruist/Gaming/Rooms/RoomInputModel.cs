/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// What a room's input is, declared by the game so the host can buffer it without knowing the
/// game (see <see cref="InputBuffer{TInput}"/>). Undeclared (<see cref="Opaque"/>), an input is a
/// value the host may only repeat or skip when it equals the one applied before.
/// <para>
/// Declared: an input has a held-buttons bitmask (<see cref="Builder.Buttons"/>) and everything else
/// (axes, aim, ...) is held state too. Some buttons act when pressed (<see cref="Builder.Action"/>):
/// a press is a rising edge of the bitmask. The simulation applies the presses of one step in a
/// fixed order of stages (a jump before a dash, say); a step can hold at most one press per stage,
/// and presses in one step happened in stage order. So an input stream says exactly which presses
/// happened and in which order, and the buffer can:
/// </para>
/// <list type="bullet">
/// <item>repeat the held state when a step is starved, never a press (a held button stays held: no new edge);</item>
/// <item>catch up after a stall or a burst by merging two inputs into one step only when no press
/// is lost and none changes order (<see cref="TryMerge"/>).</item>
/// </list>
/// </summary>
public sealed class RoomInputModel<TInput> where TInput : struct
{
    private readonly Func<TInput, int>? _buttons;
    private readonly Func<TInput, int, TInput>? _withButtons;
    private readonly Func<TInput, TInput>? _starved;
    private readonly int[] _stage = new int[32];
    /// <summary>Action bits in the order the simulation applies them within a step (stage, then declaration).</summary>
    private readonly int[] _order;
    private readonly IEqualityComparer<TInput> _eq = EqualityComparer<TInput>.Default;

    /// <summary>No declaration: inputs are opaque values (merged only when one equals the input applied before it).</summary>
    public static RoomInputModel<TInput> Opaque { get; } = new(null, null, null, Array.Empty<(int, int)>());

    private RoomInputModel(Func<TInput, int>? buttons, Func<TInput, int, TInput>? withButtons, Func<TInput, TInput>? starved,
        IReadOnlyList<(int Mask, int Stage)> actions)
    {
        _buttons = buttons;
        _withButtons = withButtons;
        _starved = starved;
        Array.Fill(_stage, -1);
        var order = new List<(int Bit, int Stage, int Index)>();
        for (var i = 0; i < actions.Count; i++)
        {
            var (mask, stage) = actions[i];
            if (stage is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(actions), "stages are 0..30");
            for (var b = 0; b < 32; b++)
            {
                if ((mask & (1 << b)) == 0) continue;
                if (_stage[b] >= 0) throw new ArgumentException($"button bit {b} is declared twice");
                _stage[b] = stage;
                order.Add((1 << b, stage, order.Count));
                ActionMask |= 1 << b;
            }
        }
        _order = order.OrderBy(o => o.Stage).ThenBy(o => o.Index).Select(o => o.Bit).ToArray();
    }

    /// <summary>Starts a declaration.</summary>
    public static Builder Describe() => new();

    public sealed class Builder
    {
        private Func<TInput, int>? _buttons;
        private Func<TInput, int, TInput>? _withButtons;
        private Func<TInput, TInput>? _starved;
        private readonly List<(int, int)> _actions = new();

        /// <summary>The held-buttons bitmask: how to read it and how to set it on a copy.</summary>
        public Builder Buttons(Func<TInput, int> get, Func<TInput, int, TInput> with)
        {
            _buttons = get;
            _withButtons = with;
            return this;
        }

        /// <summary>
        /// Buttons (bits of <paramref name="mask"/>) that act on their press, applied at
        /// <paramref name="stage"/> of the simulation step: presses of lower stages first, at most
        /// one press per stage in a step. Declaration order breaks ties between bits of one stage.
        /// </summary>
        public Builder Action(int mask, int stage)
        {
            _actions.Add((mask, stage));
            return this;
        }

        /// <summary>
        /// The input applied on a step with nothing queued, from the one applied last (default: the
        /// same input; a declared held bitmask never yields a press when repeated). Use it to clear
        /// one-shot fields that are not part of the bitmask.
        /// </summary>
        public Builder WhenStarved(Func<TInput, TInput> repeat)
        {
            _starved = repeat;
            return this;
        }

        public RoomInputModel<TInput> Build()
        {
            if (_actions.Count > 0 && _buttons is null) throw new InvalidOperationException("Action buttons need Buttons(get, with) first.");
            return new RoomInputModel<TInput>(_buttons, _withButtons, _starved, _actions);
        }
    }

    /// <summary>Every button that acts on its press.</summary>
    public int ActionMask { get; }

    /// <summary>A held-buttons bitmask is declared.</summary>
    public bool HasButtons => _buttons is not null;

    /// <summary>Stage of a single button bit (-1: not an action).</summary>
    public int StageOf(int bit) => bit == 0 || (bit & (bit - 1)) != 0 ? -1 : _stage[System.Numerics.BitOperations.TrailingZeroCount(bit)];

    /// <summary>Presses (rising action bits) of <paramref name="cur"/> after <paramref name="prev"/>.</summary>
    public int Presses(in TInput prev, in TInput cur) => _buttons is null ? 0 : _buttons(cur) & ~_buttons(prev) & ActionMask;

    /// <summary>Appends the bits of <paramref name="presses"/> in the order the simulation applies them.</summary>
    public void AppendInOrder(int presses, List<int> to)
    {
        if (presses == 0) return;
        foreach (var b in _order)
            if ((presses & b) != 0) to.Add(b);
    }

    /// <summary>The presses can share one step: at most one per stage.</summary>
    public bool FitsOneStep(int presses)
    {
        var seen = 0L;
        foreach (var b in _order)
        {
            if ((presses & b) == 0) continue;
            var s = 1L << _stage[System.Numerics.BitOperations.TrailingZeroCount(b)];
            if ((seen & s) != 0) return false;
            seen |= s;
        }
        return true;
    }

    /// <summary>The input of a starved step after <paramref name="last"/> (see <see cref="Builder.WhenStarved"/>).</summary>
    public TInput Starved(in TInput last) => _starved is null ? last : _starved(last);

    /// <summary>
    /// One input for one step that does what <paramref name="a"/> then <paramref name="b"/> would do
    /// after <paramref name="prev"/>, losing no press and keeping their order; false when none exists.
    /// The step count shrinks by one (held state of <paramref name="a"/> is skipped), which is what
    /// catching up means.
    /// </summary>
    public bool TryMerge(in TInput prev, in TInput a, in TInput b, out TInput merged)
    {
        merged = b;
        // a changes nothing: skipping it loses nothing at all.
        if (_eq.Equals(a, prev)) return true;
        if (_buttons is null) return false;
        var pa = Presses(prev, a);
        var pb = Presses(a, b);
        var bPrev = _buttons(prev);
        var bA = _buttons(a);
        var bB = _buttons(b);
        // A button let go in a and pressed again in b: b alone would show no press.
        if ((bPrev & ~bA & bB & ActionMask) != 0) return false;
        if (pa == 0) return true; // a only changed held state: b after prev has exactly b's presses
        // a pressed something: b may only add later presses on top (nothing let go, nothing else moved).
        if (bB != (bA | pb)) return false;
        if (!_eq.Equals(_withButtons!(a, 0), _withButtons(b, 0))) return false;
        if (pb != 0 && (!FitsOneStep(pa | pb) || !AllBefore(pa, pb))) return false;
        return true;
    }

    /// <summary>Every press of <paramref name="first"/> is applied before every press of <paramref name="then"/> in one step.</summary>
    private bool AllBefore(int first, int then)
    {
        var maxFirst = -1;
        var minThen = int.MaxValue;
        for (var i = 0; i < _order.Length; i++)
        {
            var b = _order[i];
            if ((first & b) != 0) maxFirst = i;
            if ((then & b) != 0 && minThen == int.MaxValue) minThen = i;
        }
        return maxFirst < minThen;
    }
}
