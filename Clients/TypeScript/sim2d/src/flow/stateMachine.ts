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

/** Convenience base for contexts (C# abstract class `AIContext`). Extend and add the agent's own state. */
export class AIContext implements StateContextCore {
  /** @inheritDoc StateContextCore.timeInState */
  timeInState = 0;
  /** @inheritDoc StateContextCore.stateDuration */
  stateDuration = 0;
  /** @inheritDoc StateContextCore.currentStateTag */
  currentStateTag = '';
  /** @inheritDoc StateContextCore.activeWindows */
  activeWindows: ReadonlyMap<string, StateWindow> | null = null;
  /** @inheritDoc StateContextCore.currentStateData */
  currentStateData: unknown = null;
  /** @inheritDoc StateContextCore.previousProgress */
  previousProgress = 0;
}

/** A state's per-tick handler: returns the name of the state to go to, or null / undefined / the
 * current name to stay (an unknown name also stays). `dt` in seconds. Mirrors the C#
 * `Func<TContext, float, string?>` update delegate. */
export type StateHandler<TCtx> = (ctx: TCtx, dt: number) => string | null | undefined;
/** A state's enter or exit hook. Mirrors the C# `Action<TContext>` hooks. */
export type StateHook<TCtx> = (ctx: TCtx) => void;

/** Immutable dispatch tables and per-state metadata, shared by every {@link StateMachine} built from
 * it (C# `StateMachineDef<TContext>`). Produced by {@link StateMachineBuilder.build}. */
export interface StateMachineDef<TCtx> {
  /** Name of the state entered by {@link StateMachine.initialize}. C# `InitialState`. */
  readonly initialState: string;
  /** Per-state handlers; a state exists only if it has one. C# `Updates`. */
  readonly updates: ReadonlyMap<string, StateHandler<TCtx>>;
  /** Per-state enter hooks. C# `Enters`. */
  readonly enters: ReadonlyMap<string, StateHook<TCtx>>;
  /** Per-state exit hooks. C# `Exits`. */
  readonly exits: ReadonlyMap<string, StateHook<TCtx>>;
  /** Seconds a state waits before its handler first runs. C# `Delays`. */
  readonly delays: ReadonlyMap<string, number>;
  /** Base duration (seconds) per state, copied into `ctx.stateDuration` on entry. C# `Durations`. */
  readonly durations: ReadonlyMap<string, number>;
  /** Tag per state, copied into `ctx.currentStateTag`. C# `Tags`. */
  readonly tags: ReadonlyMap<string, string>;
  /** Windows per state (kind → window), copied into `ctx.activeWindows`. C# `Windows`. */
  readonly windows: ReadonlyMap<string, ReadonlyMap<string, StateWindow>>;
  /** Data blob per state, copied into `ctx.currentStateData`. C# `Data`. */
  readonly data: ReadonlyMap<string, unknown>;
  /** Hook run on every state entry before the state's own enter hook (may adjust `ctx.stateDuration`).
   * C# `OnStateEnter`. */
  readonly onStateEnter: ((ctx: TCtx, state: string, baseDuration: number) => void) | null;
}

/**
 * Builds a {@link StateMachineDef}. Mirrors C# `StateMachineBuilder<TContext>` without attribute
 * scanning (`RegisterHandlers`): register each state with `configureState(name).handler(...)`.
 * @example
 * ```ts
 * const b = new StateMachineBuilder<AgentCtx>();
 * b.configureState('idle').handler((c) => (c.seesTarget ? 'attack' : null)).asInitial();
 * b.configureState('attack').duration(0.6).window('hit', 0.3, 0.5).handler((c) => {
 *   if (StateContext.windowEntered(c, 'hit')) strike(c);
 *   return StateContext.progress(c) >= 1 ? 'idle' : null;
 * });
 * const def = b.build(); // share between entities
 * const fsm = new StateMachine(def);
 * fsm.initialize(ctx);
 * fsm.update(ctx, dt); // each tick
 * ```
 */
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

  /** Configures one state (fluent; calling again for the same name edits the same state; later
   * settings overwrite earlier ones). C# `StateMachineBuilder.ConfigureState`. */
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

  /** Sets the initial state (otherwise the first state given a handler). C# `StateMachineBuilder.SetInitial`.
   * @returns this. */
  setInitial(name: string): this {
    this.initial = name;
    return this;
  }

  /** Hook on every state entry `(ctx, state, baseDuration)`, after the metadata is applied and before
   * the state's own enter hook. C# `StateMachineBuilder.OnStateEnter`. @returns this. */
  onStateEnter(hook: (ctx: TCtx, state: string, baseDuration: number) => void): this {
    this.enterHook = hook;
    return this;
  }

  /** Snapshots the tables into a {@link StateMachineDef}. Throws (`Error`) when no state has a handler
   * or the initial state has none. C# `StateMachineBuilder.Build`. */
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

/** Fluent configuration of one state, from {@link StateMachineBuilder.configureState}. Mirrors C#
 * `StateMachineBuilder<TContext>.StateConfig` (minus `Motion` / `MotionThrottle`). Every method returns
 * the same config. */
export interface StateConfig<TCtx> {
  /** Sets the per-tick handler (required: a state without one does not exist). C# `StateConfig.Handler`. */
  handler(update: StateHandler<TCtx>): StateConfig<TCtx>;
  /** Sets the enter hook. C# `StateConfig.OnEnter`. */
  onEnter(hook: StateHook<TCtx>): StateConfig<TCtx>;
  /** Sets the exit hook. C# `StateConfig.OnExit`. */
  onExit(hook: StateHook<TCtx>): StateConfig<TCtx>;
  /** Sets the base duration in seconds (enables progress and windows; does not end the state by
   * itself). C# `StateConfig.Duration`. */
  duration(seconds: number): StateConfig<TCtx>;
  /** Seconds after entry before the handler first runs. C# `StateConfig.Delay`. */
  delay(seconds: number): StateConfig<TCtx>;
  /** Sets the tag copied into `ctx.currentStateTag`. C# `StateConfig.Tag`. */
  tag(tag: string): StateConfig<TCtx>;
  /** Adds (or replaces) the window `kind` as fractions of the duration. C# `StateConfig.Window`. */
  window(kind: string, start: number, end: number): StateConfig<TCtx>;
  /** Sets the data blob copied into `ctx.currentStateData`. C# `StateConfig.Data`. */
  data(blob: unknown): StateConfig<TCtx>;
  /** Makes this the initial state. C# `StateConfig.AsInitial`. */
  asInitial(): StateConfig<TCtx>;
}

/** The FSM driver: one per entity, built from a shared definition. Mirrors C#
 * `StateMachine<TContext>`. Call {@link initialize} once before the first {@link update} (the
 * constructor only sets the state name; it applies no metadata and runs no enter hook). */
export class StateMachine<TCtx extends StateContextCore> {
  /** The shared definition. */
  readonly def: StateMachineDef<TCtx>;
  /** Name of the current state. C# `StateMachine.CurrentStateName`. */
  currentStateName: string;
  /** Seconds in the current state (mirrored into `ctx.timeInState`). C# `StateMachine.TimeInState`. */
  timeInState = 0;

  /** Starts at `def.initialState` without entering it; see {@link initialize}. C# `new StateMachine(def)`. */
  constructor(def: StateMachineDef<TCtx>) {
    this.def = def;
    this.currentStateName = def.initialState;
  }

  /** {@link advance} then {@link runState}; true when a transition occurred. Does nothing (time does
   * not advance) when the current state has no handler. C# `StateMachine.Update`. */
  update(ctx: TCtx, dt: number): boolean {
    if (!this.def.updates.has(this.currentStateName)) return false;
    this.advance(ctx, dt);
    return this.runState(ctx, dt);
  }

  /** Time accounting only: records the previous progress into `ctx.previousProgress`, then
   * `timeInState += dt`. Use with {@link runState} to interleave other work. C# `StateMachine.Advance`. */
  advance(ctx: TCtx, dt: number): void {
    const prevProgress = ctx.stateDuration > 0 ? this.timeInState / ctx.stateDuration : 0;
    this.timeInState += dt;
    ctx.timeInState = this.timeInState;
    ctx.previousProgress = prevProgress;
  }

  /** The handler only (no time advance), skipped while `timeInState < delay`; true when a transition
   * occurred. C# `StateMachine.RunState`. */
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

  /** Enters the initial state: resets time, applies metadata, runs `onStateEnter` and the enter hook
   * (no exit hook). C# `StateMachine.Initialize`. */
  initialize(ctx: TCtx): void {
    this.currentStateName = this.def.initialState;
    this.timeInState = 0;
    ctx.timeInState = 0;
    ctx.previousProgress = 0;
    this.applyMetadata(ctx, this.currentStateName);
    this.invokeEnter(ctx, this.currentStateName);
  }

  /** Forces a transition (exit, metadata, enter; re-enters the current state when it is named);
   * unknown states are ignored. C# `StateMachine.TransitionTo`. */
  transitionTo(ctx: TCtx, state: string): void {
    if (!this.def.updates.has(state)) return;
    this.doTransition(ctx, state);
  }

  /** Runs the current state's exit hook, then {@link initialize}. C# `StateMachine.Reset`. */
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

/** The AI-flavoured name (C# `AIStateMachine`, a `StateMachine<IAIContext>`): same machine, with
 * {@link AIContext} as the default context type. */
export class AIStateMachine<TCtx extends StateContextCore = AIContext> extends StateMachine<TCtx> {}
