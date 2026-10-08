/** Flow layer. State machine context and its progress / window helpers — mirror of C# `IStateContextCore`. */

/** A normalized [start, end] window within a state's duration. */
export interface StateWindow {
  readonly start: number;
  readonly end: number;
}

/** The bookkeeping the machine writes into a context (C# `IStateContextCore`). */
export interface StateContextCore {
  timeInState: number;
  stateDuration: number;
  currentStateTag: string;
  activeWindows: ReadonlyMap<string, StateWindow> | null;
  currentStateData: unknown;
  previousProgress: number;
}

/** `timeInState / stateDuration`, 0 for open-ended states. */
export function progress(ctx: StateContextCore): number {
  return ctx.stateDuration > 0 ? ctx.timeInState / ctx.stateDuration : 0;
}

/** True while progress is within the window `kind`. */
export function inWindow(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  const p = progress(ctx);
  return p >= w.start && p <= w.end;
}

/** True only on the tick progress first crossed the window start. */
export function windowEntered(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  return ctx.previousProgress < w.start && progress(ctx) >= w.start;
}

/** True only on the tick progress just passed the window end. */
export function windowExited(ctx: StateContextCore, kind: string): boolean {
  if (ctx.stateDuration <= 0 || !ctx.activeWindows) return false;
  const w = ctx.activeWindows.get(kind);
  if (!w) return false;
  return ctx.previousProgress <= w.end && progress(ctx) > w.end;
}
