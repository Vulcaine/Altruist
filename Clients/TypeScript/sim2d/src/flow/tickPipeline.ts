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

export class TickPipeline<TCtx> {
  private readonly stepNames: readonly string[];
  private readonly steps: readonly Step<TCtx>[];
  /** Index of the stop point that ended the last tick, or -1. */
  stoppedAt = -1;

  private constructor(names: readonly string[], steps: readonly Step<TCtx>[]) {
    this.stepNames = names;
    this.steps = steps;
  }

  static create<TCtx>(): TickPipelineBuilder<TCtx> {
    return new TickPipelineBuilder<TCtx>((n, s) => new TickPipeline(n, s));
  }

  get count(): number {
    return this.steps.length;
  }

  nameOf(index: number): string {
    return this.stepNames[index]!;
  }

  indexOf(name: string): number {
    return this.stepNames.indexOf(name);
  }

  /** Runs one tick; false when a stop point ended it early (see `stoppedAt`). */
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

export class TickPipelineBuilder<TCtx> extends OrderedBuilder<Step<TCtx>> {
  private readonly make: (names: readonly string[], steps: readonly Step<TCtx>[]) => TickPipeline<TCtx>;

  /** Use `TickPipeline.create()`. */
  constructor(make: (names: readonly string[], steps: readonly Step<TCtx>[]) => TickPipeline<TCtx>) {
    super();
    this.make = make;
  }

  /** A step run every tick. */
  step(name: string, step: (ctx: TCtx) => void): this {
    return this.put(name, { action: step, stop: null, deferred: null });
  }

  /** A stop point: true skips the rest of the tick (it may do work first). */
  stopWhen(name: string, stop: (ctx: TCtx) => boolean): this {
    return this.put(name, { action: null, stop, deferred: null });
  }

  /** For every entity of `entities(ctx)` (list order), run the sub-steps of `steps` — an
   * `EntitySteps` (read at build time) or a function configuring a new one. */
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

/** Sub-steps run per entity: `step`s and `skipWhen` points (true skips the rest for this entity). */
export class EntitySteps<TCtx, TEntity> extends OrderedBuilder<EntityStep<TCtx, TEntity>> {
  step(name: string, step: (ctx: TCtx, entity: TEntity) => void): this {
    return this.put(name, { action: step, skip: null });
  }

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
