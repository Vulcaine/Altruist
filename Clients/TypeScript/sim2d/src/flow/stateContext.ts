/** Flow layer. State machine context and its progress / window helpers — mirror of C# `IStateContextCore`. */

/** A normalized [start, end] window within a state's duration (0 = state start, 1 = its duration),
 * e.g. the active frames of an action. Mirrors the C# readonly struct `StateWindow`. */
export interface StateWindow {
  /** Window start as a fraction of the state's duration (inclusive). */
  readonly start: number;
  /** Window end as a fraction of the state's duration (inclusive). */
  readonly end: number;
}

/** The bookkeeping the machine writes into a context. Mirrors C# `IStateContextCore` (minus
 * `CurrentStateMotion`; the C# default-interface helpers `Progress` / `InWindow` / ... are the free
 * functions of this module). Extend {@link AIContext} for a ready-made implementation. */
export interface StateContextCore {
  /** Seconds in the current state since the last transition; set by the driver each tick. */
  timeInState: number;
  /** Base duration (seconds) of the current state, from the builder's `duration`; 0 = open-ended (no
   * progress, windows never fire). An `onStateEnter` hook may override it. */
  stateDuration: number;
  /** The current state's tag ('' when none); set on every transition. */
  currentStateTag: string;
  /** The current state's windows (kind → window), or null; set on every transition. */
  activeWindows: ReadonlyMap<string, StateWindow> | null;
  /** The current state's `data` blob, or null; set on every transition. */
  currentStateData: unknown;
  /** {@link progress} at the previous tick (set before each update); used by the window-edge helpers. */
  previousProgress: number;
}

/** Normalized progress through the current state: `timeInState / stateDuration`, 0 for open-ended
 * states (can exceed 1 when the state overstays). Mirrors C# `IStateContextCore.Progress`. */
export function progress(ctx: StateContextCore): number {
  return ctx.stateDuration > 0 ? ctx.timeInState / ctx.stateDuration : 0;
}

/** True while progress is within the window `kind` (inclusive bounds); false for open-ended states or
 * an unknown kind. Mirrors C# `IStateContextCore.InWindow`. */
export function inWindow(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  const p = progress(ctx);
  return p >= w.start && p <= w.end;
}

/** Edge-triggered: true only on the tick progress first crossed the window start
 * (`previousProgress < start && progress >= start`). Mirrors C# `IStateContextCore.WindowEntered`. */
export function windowEntered(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  return ctx.previousProgress < w.start && progress(ctx) >= w.start;
}

/** Edge-triggered: true only on the tick progress just passed the window end
 * (`previousProgress <= end && progress > end`). Mirrors C# `IStateContextCore.WindowExited`. */
export function windowExited(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  return ctx.previousProgress <= w.end && progress(ctx) > w.end;
}
