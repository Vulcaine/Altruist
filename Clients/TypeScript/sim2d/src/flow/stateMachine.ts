/**
 * Flow layer. Generic finite state machine — mirror of C# `StateMachine<TContext>`,
 * `StateMachineBuilder<TContext>`, `StateMachineDef<TContext>`, `IStateContextCore`,
 * `AIStateMachine` and `AIContext`, with the same semantics:
 *
 * - `update(ctx, dt)` = `advance(ctx, dt)` then `runState(ctx, dt)`; a state without a handler
 *   does nothing (time does not advance).
 * - `advance`: records the previous progress, then `timeInState += dt`.
 * - `runState`: skipped while `timeInState < delay`; runs the handler; a returned name of another
 *   known state transitions (Exit, metadata, the `onStateEnter` hook, Enter, time reset to 0).
 *   Returning the current state's name, null or an unknown name stays.
 * - `transitionTo(ctx, name)` forces a transition, re-entering the current state when it is named.
 *
 * Attribute scanning (C# `[State]` / `[AIState]`) has no TypeScript form: register handlers with
 * `configureState(name).handler(...)`. Motion profiles are not mirrored.
 */

import type { StateContextCore, StateWindow } from './stateContext.ts';

/** Convenience base for contexts (C# `AIContext`). Extend and add the agent's own state. */
export class AIContext implements StateContextCore {
  timeInState = 0;
  stateDuration = 0;
  currentStateTag = '';
  activeWindows: ReadonlyMap<string, StateWindow> | null = null;
  currentStateData: unknown = null;
  previousProgress = 0;
}

export type StateHandler<TCtx> = (ctx: TCtx, dt: number) => string | null | undefined;
export type StateHook<TCtx> = (ctx: TCtx) => void;

/** Immutable dispatch tables and per-state metadata (C# `StateMachineDef`). */
export interface StateMachineDef<TCtx> {
  readonly initialState: string;
  readonly updates: ReadonlyMap<string, StateHandler<TCtx>>;
  readonly enters: ReadonlyMap<string, StateHook<TCtx>>;
  readonly exits: ReadonlyMap<string, StateHook<TCtx>>;
  readonly delays: ReadonlyMap<string, number>;
  readonly durations: ReadonlyMap<string, number>;
  readonly tags: ReadonlyMap<string, string>;
  readonly windows: ReadonlyMap<string, ReadonlyMap<string, StateWindow>>;
  readonly data: ReadonlyMap<string, unknown>;
  readonly onStateEnter: ((ctx: TCtx, state: string, baseDuration: number) => void) | null;
}

export class StateMachineBuilder<TCtx extends StateContextCore> {
  private readonly updates = new Map<string, StateHandler<TCtx>>();
  private readonly enters = new Map<string, StateHook<TCtx>>();
  private readonly exits = new Map<string, StateHook<TCtx>>();
  private readonly delays = new Map<string, number>();
  private readonly durations = new Map<string, number>();
  private readonly tags = new Map<string, string>();
  private readonly windows = new Map<string, Map<string, StateWindow>>();
  private readonly data = new Map<string, unknown>();
  private initial: string | null = null;
  private enterHook: ((ctx: TCtx, state: string, baseDuration: number) => void) | null = null;

  /** Configure one state (fluent). */
  configureState(name: string): StateConfig<TCtx> {
    const b = this;
    const cfg: StateConfig<TCtx> = {
      handler(update) {
        b.updates.set(name, update);
        return cfg;
      },
      onEnter(hook) {
        b.enters.set(name, hook);
        return cfg;
      },
      onExit(hook) {
        b.exits.set(name, hook);
        return cfg;
      },
      duration(seconds) {
        b.durations.set(name, seconds);
        return cfg;
      },
      delay(seconds) {
        b.delays.set(name, seconds);
        return cfg;
      },
      tag(tag) {
        b.tags.set(name, tag);
        return cfg;
      },
      window(kind, start, end) {
        let bucket = b.windows.get(name);
        if (!bucket) b.windows.set(name, (bucket = new Map()));
        bucket.set(kind, { start, end });
        return cfg;
      },
      data(blob) {
        b.data.set(name, blob);
        return cfg;
      },
      asInitial() {
        b.initial = name;
        return cfg;
      },
    };
    return cfg;
  }

  /** The initial state (otherwise the first registered one). */
  setInitial(name: string): this {
    this.initial = name;
    return this;
  }

  /** Hook on every state entry `(ctx, state, baseDuration)`, before the state's own enter hook. */
  onStateEnter(hook: (ctx: TCtx, state: string, baseDuration: number) => void): this {
    this.enterHook = hook;
    return this;
  }

  build(): StateMachineDef<TCtx> {
    if (this.updates.size === 0) throw new Error('StateMachineBuilder has no states registered.');
    const initial = this.initial ?? this.updates.keys().next().value!;
    if (!this.updates.has(initial)) throw new Error(`Initial state '${initial}' has no registered update handler.`);
    const windows = new Map<string, ReadonlyMap<string, StateWindow>>();
    for (const [k, v] of this.windows) windows.set(k, new Map(v));
    return {
      initialState: initial,
      updates: new Map(this.updates),
      enters: new Map(this.enters),
      exits: new Map(this.exits),
      delays: new Map(this.delays),
      durations: new Map(this.durations),
      tags: new Map(this.tags),
      windows,
      data: new Map(this.data),
      onStateEnter: this.enterHook,
    };
  }
}

export interface StateConfig<TCtx> {
  handler(update: StateHandler<TCtx>): StateConfig<TCtx>;
  onEnter(hook: StateHook<TCtx>): StateConfig<TCtx>;
  onExit(hook: StateHook<TCtx>): StateConfig<TCtx>;
  duration(seconds: number): StateConfig<TCtx>;
  delay(seconds: number): StateConfig<TCtx>;
  tag(tag: string): StateConfig<TCtx>;
  window(kind: string, start: number, end: number): StateConfig<TCtx>;
  data(blob: unknown): StateConfig<TCtx>;
  asInitial(): StateConfig<TCtx>;
}

/** The FSM driver: one per entity, built from a shared definition. */
export class StateMachine<TCtx extends StateContextCore> {
  readonly def: StateMachineDef<TCtx>;
  currentStateName: string;
  timeInState = 0;

  constructor(def: StateMachineDef<TCtx>) {
    this.def = def;
    this.currentStateName = def.initialState;
  }

  /** `advance` then `runState`; true when a transition occurred. */
  update(ctx: TCtx, dt: number): boolean {
    if (!this.def.updates.has(this.currentStateName)) return false;
    this.advance(ctx, dt);
    return this.runState(ctx, dt);
  }

  /** Time accounting only: previous progress, then `timeInState += dt`. */
  advance(ctx: TCtx, dt: number): void {
    const prevProgress = ctx.stateDuration > 0 ? this.timeInState / ctx.stateDuration : 0;
    this.timeInState += dt;
    ctx.timeInState = this.timeInState;
    ctx.previousProgress = prevProgress;
  }

  /** The handler only (no time advance); true when a transition occurred. */
  runState(ctx: TCtx, dt: number): boolean {
    const update = this.def.updates.get(this.currentStateName);
    if (!update) return false;
    const delay = this.def.delays.get(this.currentStateName);
    if (delay !== undefined && this.timeInState < delay) return false;
    const next = update(ctx, dt);
    if (next != null && next !== this.currentStateName && this.def.updates.has(next)) {
      this.doTransition(ctx, next);
      return true;
    }
    return false;
  }

  /** Enters the initial state (enter hooks run). */
  initialize(ctx: TCtx): void {
    this.currentStateName = this.def.initialState;
    this.timeInState = 0;
    ctx.timeInState = 0;
    ctx.previousProgress = 0;
    this.applyMetadata(ctx, this.currentStateName);
    this.invokeEnter(ctx, this.currentStateName);
  }

  /** Forces a transition (re-enters the current state when named); unknown states are ignored. */
  transitionTo(ctx: TCtx, state: string): void {
    if (!this.def.updates.has(state)) return;
    this.doTransition(ctx, state);
  }

  /** Exit the current state, then `initialize`. */
  reset(ctx: TCtx): void {
    this.def.exits.get(this.currentStateName)?.(ctx);
    this.initialize(ctx);
  }

  private doTransition(ctx: TCtx, next: string): void {
    this.def.exits.get(this.currentStateName)?.(ctx);
    this.currentStateName = next;
    this.timeInState = 0;
    ctx.timeInState = 0;
    ctx.previousProgress = 0;
    this.applyMetadata(ctx, next);
    this.invokeEnter(ctx, next);
  }

  private applyMetadata(ctx: TCtx, state: string): void {
    ctx.currentStateTag = this.def.tags.get(state) ?? '';
    ctx.activeWindows = this.def.windows.get(state) ?? null;
    ctx.stateDuration = this.def.durations.get(state) ?? 0;
    ctx.currentStateData = this.def.data.has(state) ? this.def.data.get(state) : null;
  }

  private invokeEnter(ctx: TCtx, state: string): void {
    if (this.def.onStateEnter) this.def.onStateEnter(ctx, state, this.def.durations.get(state) ?? 0);
    this.def.enters.get(state)?.(ctx);
  }
}

/** The AI-flavoured name (C# `AIStateMachine`): same machine. */
export class AIStateMachine<TCtx extends StateContextCore = AIContext> extends StateMachine<TCtx> {}
