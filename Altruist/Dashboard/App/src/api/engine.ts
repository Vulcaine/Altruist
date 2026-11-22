import type { Transport } from '@/host/transport';

export interface FrameReport {
  readonly targetHz: number;
  readonly framesPerSecond: number;
  readonly lastMs: number;
  readonly averageMs: number;
  readonly p95Ms: number;
  readonly maxMs: number;
  readonly overBudget: number;
  readonly window: number;
}

export interface StepperTiming {
  readonly name: string;
  readonly samples: number;
  readonly averageMs: number;
  readonly p95Ms: number;
  readonly maxMs: number;
  readonly fixedStepsPerSecond: number;
  readonly overruns: number;
}

export interface StepperReport {
  readonly name: string;
  readonly mode: string;
  readonly fixedHz: number;
  readonly totalSteps: number | null;
  readonly timing: StepperTiming | null;
}

export interface CycleTask {
  readonly name: string;
  readonly rate: string;
  readonly runs: number;
  readonly faults: number;
  readonly lastRunUtc: string | null;
  readonly lastMs: number;
  readonly averageMs: number;
  readonly maxMs: number;
  readonly running: boolean;
  readonly lastFault: string | null;
}

export interface CronJob {
  readonly name: string;
  readonly expression: string;
  readonly nextUtc: string | null;
  readonly lastRunUtc: string | null;
  readonly runs: number;
  readonly faults: number;
  readonly lastMs: number;
  readonly running: boolean;
  readonly lastFault: string | null;
}

export interface EngineFault {
  readonly source: string;
  readonly count: number;
  readonly lastUtc: string;
  readonly lastMessage: string;
}

export interface EngineReport {
  readonly running: boolean;
  readonly frame: number;
  readonly worldStep: string | null;
  readonly frames: FrameReport | null;
  readonly worldFrame: StepperTiming | null;
  readonly coordinator: { frame: number; maxFrameDelta: number; maxStepsPerFrame: number; overrun: string } | null;
  readonly stepWorkers: number | null;
  readonly steppers: readonly StepperReport[];
  readonly cycleTasks: readonly CycleTask[];
  readonly cronJobs: readonly CronJob[];
  readonly faults: readonly EngineFault[];
}

export const engineApi = (t: Transport) => ({
  report: () => t.json<EngineReport>({ method: 'GET', path: '/dashboard/v1/engine' }),
});
