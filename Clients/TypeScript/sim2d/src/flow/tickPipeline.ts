/**
 * Flow layer. The ordered, named phases of one simulation tick — mirror of C# `TickPipeline<TCtx>`
 * and `EntitySteps<TCtx, TEntity>`. Steps run in build order: plain steps, stop points (`stopWhen`:
 * true ends the tick), and per-entity blocks (`forEach`) that run all their sub-steps for one
 * entity before the next, in list order (index loop, length re-read each iteration). Sub-step
 * `skipWhen` is the `continue` of a `for…of`. Editable by name at setup.
 */
import { OrderedBuilder } from './orderedBuilder.ts';

interface Step<TCtx> {
  action: ((ctx: TCtx) => void) | null;
  stop: ((ctx: TCtx) => boolean) | null;
  deferred: (() => (ctx: TCtx) => void) | null;
}

/**
 * The ordered, named phases of one simulation tick. Mirrors C# `TickPipeline<TCtx>`. Replaces a
 * hand-written `tick()` body so phases can be inserted / replaced by name and the order is explicit.
 * @typeParam TCtx - Whatever the steps read and write (the simulation state).
 * @example
 * ```ts
 * const tick = TickPipeline.create<Sim>()
 *   .step('input', (s) => s.readInputs())
 *   .stopWhen('paused', (s) => s.paused)
 *   .forEach('movers', (s) => s.movers, (e) => e
 *     .skipWhen('inactive', (s, m) => !m.active)
 *     .step('steer', (s, m) => steer(s, m)))
 *   .step('physics', (s) => s.world.step(s.dt))
 *   .build();
 * tick.run(sim);
 * ```
 */
export class TickPipeline<TCtx> {
  private readonly stepNames: readonly string[];
  private readonly steps: readonly Step<TCtx>[];
  /** Index of the stop point that ended the last tick, or -1 when it ran to the end. C# returns it via
   * `Run(ctx, out stoppedAt)`. */
  stoppedAt = -1;

  private constructor(names: readonly string[], steps: readonly Step<TCtx>[]) {
    this.stepNames = names;
    this.steps = steps;
  }

  /** Starts a builder. Mirrors C# `TickPipeline<TCtx>.Create`. */
  static create<TCtx>(): TickPipelineBuilder<TCtx> {
    return new TickPipelineBuilder<TCtx>((n, s) => new TickPipeline(n, s));
  }

  /** Number of top-level steps (a `forEach` block counts as one). C# `TickPipeline.Count`. */
  get count(): number {
    return this.steps.length;
  }

  /** Name of the step at `index` (e.g. `nameOf(stoppedAt)`). C# `TickPipeline.NameOf`. */
  nameOf(index: number): string {
    return this.stepNames[index]!;
  }

  /** Index of the step named `name`, or -1. C# `TickPipeline.IndexOf`. */
  indexOf(name: string): number {
    return this.stepNames.indexOf(name);
  }

  /** Runs one tick: every step in order; false when a stop point ended it early (see
   * {@link stoppedAt}). C# `TickPipeline.Run`. */
  run(ctx: TCtx): boolean {
    const steps = this.steps;
    for (let i = 0; i < steps.length; i++) {
      const s = steps[i]!;
      if (s.stop) {
        if (s.stop(ctx)) {
          this.stoppedAt = i;
          return false;
        }
        continue;
      }
      s.action!(ctx);
    }
    this.stoppedAt = -1;
    return true;
  }
}

/** Builder of a {@link TickPipeline}; get one from {@link TickPipeline.create}. Mirrors C#
 * `TickPipeline<TCtx>.Builder`. */
export class TickPipelineBuilder<TCtx> extends OrderedBuilder<Step<TCtx>> {
  private readonly make: (names: readonly string[], steps: readonly Step<TCtx>[]) => TickPipeline<TCtx>;

  /** Internal: use {@link TickPipeline.create}. */
  constructor(make: (names: readonly string[], steps: readonly Step<TCtx>[]) => TickPipeline<TCtx>) {
    super();
    this.make = make;
  }

  /** Adds a step run every tick. C# `Builder.Step`. @returns this. */
  step(name: string, step: (ctx: TCtx) => void): this {
    return this.put(name, { action: step, stop: null, deferred: null });
  }

  /** Adds a stop point: returning true skips the rest of the tick (it may do work first). C#
   * `Builder.StopWhen`. @returns this. */
  stopWhen(name: string, stop: (ctx: TCtx) => boolean): this {
    return this.put(name, { action: null, stop, deferred: null });
  }

  /** For every entity of `entities(ctx)` (list order), run the sub-steps of `steps` — an
   * {@link EntitySteps} (read at build time, so later edits to it before `build()` count) or a function
   * configuring a new one. The list is fetched once per tick; its length is re-read each iteration.
   * Mirrors both C# `Builder.ForEach` overloads. @returns this. */
  forEach<TEntity>(
    name: string,
    entities: (ctx: TCtx) => readonly TEntity[],
    steps: EntitySteps<TCtx, TEntity> | ((s: EntitySteps<TCtx, TEntity>) => void),
  ): this {
    let sub: EntitySteps<TCtx, TEntity>;
    if (typeof steps === 'function') {
      sub = new EntitySteps<TCtx, TEntity>();
      steps(sub);
    } else sub = steps;
    return this.put(name, { action: null, stop: null, deferred: () => sub.compile(entities) });
  }

  /** Freezes the steps (and compiles each `forEach` block) into a {@link TickPipeline}. C# `Builder.Build`. */
  build(): TickPipeline<TCtx> {
    const { names, entries } = this.snapshot();
    const steps = entries.map((e) => (e.deferred ? { action: e.deferred(), stop: null, deferred: null } : e));
    return this.make(names, steps);
  }
}

interface EntityStep<TCtx, TEntity> {
  action: ((ctx: TCtx, e: TEntity) => void) | null;
  skip: ((ctx: TCtx, e: TEntity) => boolean) | null;
}

/** Sub-steps run per entity inside {@link TickPipelineBuilder.forEach}: `step`s and `skipWhen` points
 * (true skips the rest for this entity, like `continue`). Editable by name like any
 * {@link OrderedBuilder}. Mirrors C# `EntitySteps<TCtx, TEntity>`. */
export class EntitySteps<TCtx, TEntity> extends OrderedBuilder<EntityStep<TCtx, TEntity>> {
  /** Adds a sub-step run for each entity. C# `EntitySteps.Step`. @returns this. */
  step(name: string, step: (ctx: TCtx, entity: TEntity) => void): this {
    return this.put(name, { action: step, skip: null });
  }

  /** Adds a skip point: true skips this entity's remaining sub-steps. C# `EntitySteps.SkipWhen`. @returns this. */
  skipWhen(name: string, skip: (ctx: TCtx, entity: TEntity) => boolean): this {
    return this.put(name, { action: null, skip });
  }

  /** @internal Builds the per-tick loop. */
  compile(entities: (ctx: TCtx) => readonly TEntity[]): (ctx: TCtx) => void {
    const steps = this.snapshot().entries;
    return (ctx) => {
      const list = entities(ctx);
      for (let i = 0; i < list.length; i++) {
        const e = list[i]!;
        for (let k = 0; k < steps.length; k++) {
          const s = steps[k]!;
          if (s.skip) {
            if (s.skip(ctx, e)) break;
            continue;
          }
          s.action!(ctx, e);
        }
      }
    };
  }
}
