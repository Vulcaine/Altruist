import { useRef } from 'preact/hooks';
import { type CronJob, type CycleTask, type EngineFault, engineApi, type StepperReport } from '@/api/engine';
import { useTransport } from '@/app/context';
import { useResource } from '@/app/resource';
import { Badge, Card, Empty, KeyValues, Stat, Stats } from '@/ui/basics';
import { type Column, DataTable } from '@/ui/DataTable';
import { formatAgo, formatMs, formatNumber, formatTime } from '@/ui/format';
import { Page } from '@/ui/Page';
import { ResourceView, Sparkline } from '@/ui/ResourceView';

const TASK_COLUMNS: readonly Column<CycleTask>[] = [
  { id: 'name', label: 'Task', value: (t) => t.name },
  { id: 'rate', label: 'Rate', value: (t) => t.rate, width: '120px' },
  { id: 'runs', label: 'Runs', value: (t) => t.runs, align: 'right', width: '90px' },
  { id: 'avg', label: 'Avg', value: (t) => t.averageMs, align: 'right', width: '80px' },
  { id: 'max', label: 'Max', value: (t) => t.maxMs, align: 'right', width: '80px' },
  { id: 'last', label: 'Last run', value: (t) => t.lastRunUtc, width: '110px' },
  { id: 'faults', label: 'Faults', value: (t) => t.faults, align: 'right', width: '80px' },
];

const CRON_COLUMNS: readonly Column<CronJob>[] = [
  { id: 'name', label: 'Job', value: (j) => j.name },
  { id: 'expression', label: 'Schedule', value: (j) => j.expression, width: '140px' },
  { id: 'next', label: 'Next', value: (j) => j.nextUtc, width: '170px' },
  { id: 'last', label: 'Last run', value: (j) => j.lastRunUtc, width: '110px' },
  { id: 'runs', label: 'Runs', value: (j) => j.runs, align: 'right', width: '80px' },
  { id: 'faults', label: 'Faults', value: (j) => j.faults, align: 'right', width: '80px' },
];

const STEPPER_COLUMNS: readonly Column<StepperReport>[] = [
  { id: 'name', label: 'Stepper', value: (s) => s.name },
  { id: 'mode', label: 'Mode', value: (s) => s.mode, width: '110px' },
  { id: 'hz', label: 'Fixed rate', value: (s) => s.fixedHz, align: 'right', width: '100px' },
  { id: 'steps', label: 'Steps/s', value: (s) => s.timing?.fixedStepsPerSecond, align: 'right', width: '90px' },
  { id: 'avg', label: 'Avg', value: (s) => s.timing?.averageMs, align: 'right', width: '80px' },
  { id: 'p95', label: 'p95', value: (s) => s.timing?.p95Ms, align: 'right', width: '80px' },
  { id: 'max', label: 'Max', value: (s) => s.timing?.maxMs, align: 'right', width: '80px' },
  { id: 'overruns', label: 'Overruns', value: (s) => s.timing?.overruns, align: 'right', width: '90px' },
];

const FAULT_COLUMNS: readonly Column<EngineFault>[] = [
  { id: 'source', label: 'Source', value: (f) => f.source },
  { id: 'count', label: 'Count', value: (f) => f.count, align: 'right', width: '80px' },
  { id: 'last', label: 'Last', value: (f) => f.lastUtc, width: '110px' },
  { id: 'message', label: 'Last message', value: (f) => f.lastMessage },
];

/** The game loop: frame time against its budget, world steppers, [Cycle] tasks, cron jobs and failures. */
export function EngineView() {
  const api = engineApi(useTransport());
  const history = useRef<number[]>([]);
  const [report, reload] = useResource(
    () =>
      api.report().then((r) => {
        if (r.frames) {
          history.current.push(r.frames.averageMs);
          if (history.current.length > 60) history.current.shift();
        }
        return r;
      }),
    [],
    2000,
  );

  return (
    <Page title="Engine" subtitle="Frame timing, world steppers, scheduled tasks and failures. Refreshes every 2 s." resource={report} reload={reload}>
      <ResourceView resource={report} reload={reload}>
        {(r) => {
          const budget = r.frames && r.frames.targetHz > 0 ? 1000 / r.frames.targetHz : null;
          return (
            <>
              <Stats>
                <Stat label="Loop" value={r.running ? <Badge tone="ok">running</Badge> : <Badge tone="danger">stopped</Badge>} hint={r.worldStep ? `world steps: ${r.worldStep.toLowerCase()}` : undefined} />
                <Stat label="Frame" value={formatNumber(r.frame)} hint={r.frames ? `${formatNumber(r.frames.framesPerSecond)} / ${r.frames.targetHz} per second` : undefined} tone={r.frames && r.frames.framesPerSecond < r.frames.targetHz * 0.9 ? 'warn' : undefined} />
                <Stat
                  label="Frame work"
                  value={r.frames ? formatMs(r.frames.averageMs) : '—'}
                  hint={<Sparkline values={history.current} width={110} height={18} />}
                  tone={budget && r.frames && r.frames.p95Ms > budget ? 'danger' : undefined}
                />
                <Stat label="Frame p95" value={r.frames ? formatMs(r.frames.p95Ms) : '—'} hint={r.frames ? `max ${formatMs(r.frames.maxMs)}${budget ? ` · budget ${formatMs(budget)}, ${r.frames.overBudget} over` : ''}` : undefined} />
                <Stat label="World step" value={r.worldFrame ? formatMs(r.worldFrame.averageMs) : '—'} hint={r.worldFrame ? `p95 ${formatMs(r.worldFrame.p95Ms)}` : undefined} />
                <Stat label="Failures" value={formatNumber(r.faults.reduce((n, f) => n + f.count, 0))} tone={r.faults.length ? 'danger' : 'ok'} />
              </Stats>
              {r.coordinator && (
                <Card title="World steppers" actions={r.stepWorkers ? <Badge>{r.stepWorkers} step worker{r.stepWorkers === 1 ? '' : 's'}</Badge> : undefined} flush>
                  <div class="card-note">
                    <KeyValues
                      items={[
                        ['Coordinator frame', formatNumber(r.coordinator.frame)],
                        ['Max frame delta', `${r.coordinator.maxFrameDelta} s`],
                        ['Max steps per frame', `${r.coordinator.maxStepsPerFrame} (on overrun: ${r.coordinator.overrun})`],
                      ]}
                    />
                  </div>
                  <DataTable
                    rows={r.steppers}
                    columns={STEPPER_COLUMNS}
                    rowKey={(s) => s.name}
                    filterable={false}
                    empty="No world stepper."
                    render={(s, c) => {
                      if (c.id === 'name') return <span class="mono">{s.name}</span>;
                      if (c.id === 'mode') return <Badge>{s.mode}</Badge>;
                      if (c.id === 'hz') return s.fixedHz ? `${s.fixedHz} Hz` : '—';
                      if (c.id === 'steps') return s.timing ? formatNumber(s.timing.fixedStepsPerSecond, 1) : '—';
                      if (c.id === 'overruns') return s.timing?.overruns ? <Badge tone="warn">{s.timing.overruns}</Badge> : '0';
                      return formatMs(c.value(s) as number | undefined);
                    }}
                  />
                </Card>
              )}
              <Card title={`[Cycle] tasks (${r.cycleTasks.length})`} flush>
                <DataTable
                  rows={r.cycleTasks}
                  columns={TASK_COLUMNS}
                  rowKey={(t) => `${t.name}|${t.rate}`}
                  initialSort={{ column: 'avg', descending: true }}
                  empty="No [Cycle] task."
                  render={(t, c) => {
                    switch (c.id) {
                      case 'name':
                        return (
                          <div>
                            <span class="mono">{t.name}</span> {t.running && <Badge tone="accent">running</Badge>}
                            {t.lastFault && <div class="error-text">{t.lastFault}</div>}
                          </div>
                        );
                      case 'rate':
                        return t.rate;
                      case 'runs':
                        return formatNumber(t.runs);
                      case 'last':
                        return formatAgo(t.lastRunUtc);
                      case 'faults':
                        return t.faults ? <Badge tone="danger">{t.faults}</Badge> : '0';
                      default:
                        return formatMs(c.value(t) as number);
                    }
                  }}
                />
              </Card>
              <Card title={`Cron jobs (${r.cronJobs.length})`} flush>
                <DataTable
                  rows={r.cronJobs}
                  columns={CRON_COLUMNS}
                  rowKey={(j) => `${j.name}|${j.expression}`}
                  filterable={r.cronJobs.length > 8}
                  empty="No cron job."
                  render={(j, c) => {
                    switch (c.id) {
                      case 'name':
                        return (
                          <div>
                            <span class="mono">{j.name}</span> {j.running && <Badge tone="accent">running</Badge>}
                            {j.lastFault && <div class="error-text">{j.lastFault}</div>}
                          </div>
                        );
                      case 'expression':
                        return <code>{j.expression}</code>;
                      case 'next':
                        return j.nextUtc ? `${new Date(j.nextUtc).toLocaleDateString()} ${formatTime(j.nextUtc)}` : 'never';
                      case 'last':
                        return formatAgo(j.lastRunUtc);
                      case 'faults':
                        return j.faults ? <Badge tone="danger">{j.faults}</Badge> : '0';
                      default:
                        return formatNumber(c.value(j) as number);
                    }
                  }}
                />
              </Card>
              <Card title="Failures" flush>
                {r.faults.length === 0 ? (
                  <Empty icon="pass">Nothing has failed.</Empty>
                ) : (
                  <DataTable
                    rows={r.faults}
                    columns={FAULT_COLUMNS}
                    rowKey={(f) => f.source}
                    filterable={false}
                    render={(f, c) => (c.id === 'last' ? formatAgo(f.lastUtc) : c.id === 'count' ? <Badge tone="danger">{f.count}</Badge> : <span class={c.id === 'message' ? 'error-text' : 'mono'}>{String(c.value(f))}</span>)}
                  />
                )}
              </Card>
            </>
          );
        }}
      </ResourceView>
    </Page>
  );
}
