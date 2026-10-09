/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Flow;

/// <summary>
/// The ordered, named phases of one simulation tick: the replacement for a long <c>Step()</c>
/// method whose order is only visible by reading it. Each tick runs the steps in the order they
/// were built; a step can be a plain action, a stop point (ends the tick early, e.g. "the world
/// is frozen during the countdown"), or a per-entity block that runs its own sub-steps for every
/// entity of a list, in list order (use an <see cref="EntityRegistry{TKey, T}"/> for id order).
///
/// <para>Setup is editable by name (see <see cref="OrderedBuilder{TSelf, TEntry}"/>): a mode or
/// a test can insert a step before / after another, replace or remove one, without touching the
/// rest. After <see cref="Builder.Build"/> the order is fixed.</para>
///
/// <para>Determinism: a plain loop over arrays; per-entity blocks iterate the list by index
/// (<c>for i in 0..Count</c>, the count re-read each iteration) and run every sub-step for one
/// entity before the next entity — exactly the shape of
/// <c>foreach (var v in vehicles) { stepA(v); if (skip) continue; stepB(v); }</c>.</para>
///
/// <para>Allocation: none per tick (as long as the step delegates and the entity list accessor
/// do not allocate; <c>List&lt;T&gt;</c> through <see cref="IReadOnlyList{T}"/> does not).</para>
///
/// <code>
/// var vehicle = new EntitySteps&lt;Simulation, Vehicle&gt;()
///     .SkipWhen("demolished", (s, v) =&gt; s.TickDemolished(v))   // true = rest skipped for v
///     .Step("input",          (s, v) =&gt; s.ReadInput(v))
///     .SkipWhen("frozen",     (s, v) =&gt; s.Frozen)
///     .Step("movement",       (s, v) =&gt; s.Move(v));
/// var tick = TickPipeline&lt;Simulation&gt;.Create()
///     .Step("clock",          s =&gt; s.AdvanceClock())
///     .ForEach("vehicles",    s =&gt; s.Vehicles, vehicle)
///     .StopWhen("frozen",     s =&gt; s.Frozen &amp;&amp; s.UpdateFrozenPhase())
///     .Step("physics",        s =&gt; s.World.Step(s.Dt))
///     .Build();
/// tick.Run(sim);
/// </code>
///
/// <para>Choosing: use this when EVERY step runs each tick in a fixed order (optionally stopping
/// early); use <see cref="FirstMatch{TCtx}"/> when only the first matching branch should run.</para>
/// </summary>
public sealed class TickPipeline<TCtx>
{
    private readonly string[] _names;
    private readonly Entry[] _steps;

    /// <summary>One step (opaque; built by <see cref="Builder"/>).</summary>
    public readonly struct Entry
    {
        internal readonly Action<TCtx>? Action;
        internal readonly Func<TCtx, bool>? Stop;
        internal readonly Func<Action<TCtx>>? Deferred;

        internal Entry(Action<TCtx>? action, Func<TCtx, bool>? stop, Func<Action<TCtx>>? deferred)
        {
            Action = action;
            Stop = stop;
            Deferred = deferred;
        }
    }

    private TickPipeline(string[] names, Entry[] steps)
    {
        _names = names;
        _steps = steps;
    }

    /// <summary>Starts a new pipeline.</summary>
    public static Builder Create() => new();

    /// <summary>Number of top-level steps.</summary>
    public int Count => _steps.Length;

    /// <summary>The name of the step at <paramref name="index"/> (run order).</summary>
    public string NameOf(int index) => _names[index];

    /// <summary>Index of the step named <paramref name="name"/>, or -1.</summary>
    public int IndexOf(string name) => Array.IndexOf(_names, name);

    /// <summary>Runs one tick. Returns false when a stop point ended it early.</summary>
    public bool Run(TCtx context) => Run(context, out _);

    /// <summary>Runs one tick. <paramref name="stoppedAt"/> is the index of the stop point that
    /// ended it, or -1 when every step ran.</summary>
    public bool Run(TCtx context, out int stoppedAt)
    {
        var steps = _steps;
        for (var i = 0; i < steps.Length; i++)
        {
            ref readonly var s = ref steps[i];
            if (s.Stop is not null)
            {
                if (s.Stop(context))
                {
                    stoppedAt = i;
                    return false;
                }
                continue;
            }
            s.Action!(context);
        }
        stoppedAt = -1;
        return true;
    }

    /// <summary>Builds a <see cref="TickPipeline{TCtx}"/>.</summary>
    public sealed class Builder : OrderedBuilder<Builder, Entry>
    {
        internal Builder() { }

        /// <summary>A step run every tick.</summary>
        public Builder Step(string name, Action<TCtx> step)
        {
            ArgumentNullException.ThrowIfNull(step);
            return Put(name, new Entry(step, null, null));
        }

        /// <summary>A stop point: when <paramref name="stop"/> returns true the rest of the tick is
        /// skipped. The function may do work first (e.g. update the frozen phase, then return true).</summary>
        public Builder StopWhen(string name, Func<TCtx, bool> stop)
        {
            ArgumentNullException.ThrowIfNull(stop);
            return Put(name, new Entry(null, stop, null));
        }

        /// <summary>For every entity of <paramref name="entities"/> (list order), run all of
        /// <paramref name="steps"/>' sub-steps. The sub-steps are read when the pipeline is built, so
        /// <paramref name="steps"/> can still be edited until then.</summary>
        public Builder ForEach<TEntity>(string name, Func<TCtx, IReadOnlyList<TEntity>> entities, EntitySteps<TCtx, TEntity> steps)
        {
            ArgumentNullException.ThrowIfNull(entities);
            ArgumentNullException.ThrowIfNull(steps);
            return Put(name, new Entry(null, null, () => steps.Compile(entities)));
        }

        /// <summary>For every entity of <paramref name="entities"/> (list order), run the sub-steps
        /// configured by <paramref name="configure"/>.</summary>
        public Builder ForEach<TEntity>(string name, Func<TCtx, IReadOnlyList<TEntity>> entities, Action<EntitySteps<TCtx, TEntity>> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var steps = new EntitySteps<TCtx, TEntity>();
            configure(steps);
            return ForEach(name, entities, steps);
        }

        /// <summary>An immutable pipeline with the steps in their current order.</summary>
        public TickPipeline<TCtx> Build()
        {
            var (names, entries) = Snapshot();
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].Deferred is { } compile)
                    entries[i] = new Entry(compile(), null, null);
            return new TickPipeline<TCtx>(names, entries);
        }
    }
}

/// <summary>
/// The sub-steps a <see cref="TickPipeline{TCtx}"/> runs for each entity of a list: plain steps
/// and skip points (<see cref="SkipWhen"/>: true skips the remaining sub-steps for THIS entity,
/// the <c>continue</c> of a <c>foreach</c>). Editable by name like every flow builder.
/// </summary>
public sealed class EntitySteps<TCtx, TEntity> : OrderedBuilder<EntitySteps<TCtx, TEntity>, EntitySteps<TCtx, TEntity>.Entry>
{
    /// <summary>One sub-step (opaque).</summary>
    public readonly struct Entry
    {
        internal readonly Action<TCtx, TEntity>? Action;
        internal readonly Func<TCtx, TEntity, bool>? Skip;

        internal Entry(Action<TCtx, TEntity>? action, Func<TCtx, TEntity, bool>? skip)
        {
            Action = action;
            Skip = skip;
        }
    }

    /// <summary>A sub-step run for each entity.</summary>
    public EntitySteps<TCtx, TEntity> Step(string name, Action<TCtx, TEntity> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Put(name, new Entry(step, null));
    }

    /// <summary>A skip point: true skips the remaining sub-steps for this entity (the next entity
    /// still runs). The function may do work first.</summary>
    public EntitySteps<TCtx, TEntity> SkipWhen(string name, Func<TCtx, TEntity, bool> skip)
    {
        ArgumentNullException.ThrowIfNull(skip);
        return Put(name, new Entry(null, skip));
    }

    /// <summary>Runs the sub-steps for one entity. Returns false when a skip point cut it short.</summary>
    internal static bool RunFor(Entry[] steps, TCtx context, TEntity entity)
    {
        for (var k = 0; k < steps.Length; k++)
        {
            ref readonly var s = ref steps[k];
            if (s.Skip is not null)
            {
                if (s.Skip(context, entity)) return false;
                continue;
            }
            s.Action!(context, entity);
        }
        return true;
    }

    internal Action<TCtx> Compile(Func<TCtx, IReadOnlyList<TEntity>> entities)
    {
        var (_, steps) = Snapshot();
        return context =>
        {
            var list = entities(context);
            for (var i = 0; i < list.Count; i++) RunFor(steps, context, list[i]);
        };
    }
}
