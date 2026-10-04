using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

using Altruist.Gaming;

using Cronos;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Engine;

[Service(typeof(IEngineCore))]
[ConditionalOnConfig("altruist:game:engine")]
public class AltruistEngine : IAltruistEngine
{
    /// <summary>Frame counter of the most recently started engine frame (any engine instance).</summary>
    public static long CurrentTick { get; private set; } = 0;

    private static readonly CycleRate EveryFrame = new(1, CycleUnit.Ticks);

    /// <summary>Engine frames run by this instance (the frame being run, while inside one).</summary>
    public long Frame => Interlocked.Read(ref _frame);
    private long _frame;

    private readonly IEngineClock _clock;

    /// <summary>
    /// <c>altruist:game:engine:world-step</c>: <c>inline</c> steps the world on the engine loop after
    /// the next-tick queue, the <c>[Cycle]</c> tasks, the dynamic tasks and the effects of each frame;
    /// <c>worker</c> (default) steps it on a separate world-worker task with the summed frame time.
    /// </summary>
    public WorldStepMode WorldStep { get; }

    private readonly int _engineHz;
    private TimeSpan EngineTickPeriod => TimeSpan.FromSeconds(1.0 / _engineHz);

    private readonly IServiceProvider _serviceProvider;
    private readonly IServerStatus _appStatus;
    private readonly IGameWorldOrganizer _worldCoordinator;

    // Keep Rate for external visibility, but DO NOT use it to build the engine timer when Unit==Ticks.
    private readonly CycleRate _engineRate;
    public CycleRate Rate => _engineRate;

    public bool Enabled { get; private set; }
    public int Throttle = 1_000_000;

    private CancellationTokenSource? _cts = new();
    private CancellationTokenSource? _linkedCts;
    private Thread? _engineThread;

    // -------- Next-tick (run once next tick, sequential) --------
    private readonly ConcurrentQueue<Delegate> _nextTickQueue = new();

    // -------- Physics dt (summed until the worker picks it up) --------
    // The channel only signals "a frame happened"; the frame dts are summed in
    // _pendingPhysicsDt so a worker that falls behind steps the missed time
    // instead of losing it.
    private readonly Channel<bool> _physicsTicks = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly object _physicsDtLock = new();
    private double _pendingPhysicsDt;

    // -------- Static tasks (registered once, run on cadence, never overlap per task) --------
    // Registrations may arrive from another thread after the engine started (e.g. a
    // [PostConstruct] that runs after ServerStatus went alive); they are queued and
    // adopted by the engine thread at the start of the next frame.
    private readonly List<EngineStaticTask> _staticTasks = new();
    private readonly ConcurrentQueue<EngineStaticTask> _pendingStaticTasks = new();
    private readonly Dictionary<TaskIdentifier, long> _staticLastRunStopwatch = new(); // for time-based
    private readonly Dictionary<TaskIdentifier, long> _staticLastRunFrame = new();     // for frame-based
    private readonly Dictionary<TaskIdentifier, Task> _staticInFlight = new();

    // -------- Effects (repeating effects and one-shot timers) --------
    // _effects is the lookup for cancellation; the engine loop runs them in scheduling order
    // (_effectOrder, adopted from _pendingEffects at the start of the effect phase).
    private readonly ConcurrentDictionary<TaskIdentifier, DynamicEffectTask> _effects = new();
    private readonly ConcurrentQueue<DynamicEffectTask> _pendingEffects = new();
    private readonly List<DynamicEffectTask> _effectOrder = new();

    // -------- Dynamic tasks (requested at runtime; each request must run at least once; never overlap per id) --------
    private sealed class DynamicTaskState
    {
        public Delegate Delegate = default!;
        public int Pending;               // executions owed
        public Task? InFlight;            // currently running
    }

    private readonly ConcurrentDictionary<TaskIdentifier, DynamicTaskState> _dynamic = new();
    private const int MaxDynamicStartsPerTick = 128;

    // -------- Fault reporting (a throwing task never stops the loop) --------
    private ILogger? _logger;
    private ILogger Logger => _logger ??= (_serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
        .CreateLogger<AltruistEngine>();
    private readonly ConcurrentDictionary<string, FaultLog> _faults = new();
    private static readonly long FaultLogInterval = Stopwatch.Frequency * 10;

    private sealed class FaultLog
    {
        public long LastLogged;
        public long Suppressed;
    }

    public AltruistEngine(
        IServerStatus serverStatus,
        IServiceProvider serviceProvider,
        IGameWorldOrganizer worldCoordinator,
        [AppConfigValue("altruist:game:engine:framerateHz")] int? framerateHz = null,
        [AppConfigValue("altruist:game:engine:unit")] CycleUnit unit = CycleUnit.Ticks,
        [AppConfigValue("altruist:game:engine:throttle")] int? throttle = null,
        // Examples and docs use `frequency`; accept it as an alias for framerateHz.
        [AppConfigValue("altruist:game:engine:frequency")] int? frequency = null,
        [AppConfigValue("altruist:game:engine:world-step", "worker")] string? worldStep = null,
        IEngineClock? clock = null)
    {
        var engineFrequencyHz = framerateHz ?? frequency ?? 30;
        _serviceProvider = serviceProvider;
        _appStatus = serverStatus;
        _worldCoordinator = worldCoordinator;
        _clock = clock ?? StopwatchEngineClock.Instance;
        WorldStep = ParseWorldStep(worldStep);

        _engineHz = Math.Max(1, engineFrequencyHz);
        _engineRate = new CycleRate(engineFrequencyHz, unit); // visibility/config only

        Throttle = throttle ?? (int)(1_000_000_000 / (_engineRate.Value + 1));
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;

    public static WorldStepMode ParseWorldStep(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("worker", StringComparison.OrdinalIgnoreCase))
            return WorldStepMode.Worker;
        if (v.Equals("inline", StringComparison.OrdinalIgnoreCase))
            return WorldStepMode.Inline;
        throw new ArgumentException($"Unknown altruist:game:engine:world-step '{value}'. Use worker or inline.", nameof(value));
    }

    // ---------------- Public scheduling APIs ----------------

    public void WaitForNextTick(Delegate task)
    {
        if (task is null)
            throw new ArgumentNullException(nameof(task));
        _nextTickQueue.Enqueue(task);
    }

    public void WaitForNextTick(Action task)
    {
        if (task is null)
            throw new ArgumentNullException(nameof(task));
        _nextTickQueue.Enqueue(task);
    }

    public void WaitForNextTick(Func<Task> task)
    {
        if (task is null)
            throw new ArgumentNullException(nameof(task));
        _nextTickQueue.Enqueue(task);
    }

    public void SyncCommit(Action commit)
    {
        if (commit is null)
            throw new ArgumentNullException(nameof(commit));
        WaitForNextTick(commit);
    }

    public Task<T> SyncCommit<T>(Func<T> commit)
    {
        if (commit is null)
            throw new ArgumentNullException(nameof(commit));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        WaitForNextTick(() =>
        {
            try
            { tcs.TrySetResult(commit()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    // ---------------- Effects ----------------

    public TaskIdentifier ScheduleEffect(CycleRate cycleRate, DateTime expiresAtUtc, Action<float> step)
    {
        if (step is null)
            throw new ArgumentNullException(nameof(step));

        var id = new TaskIdentifier("Effect_" + Guid.NewGuid());

        long nowStopwatch = _clock.NowTicks;

        var effect = new DynamicEffectTask(
            id: id,
            rate: cycleRate,
            expiresAtUtc: expiresAtUtc,
            step: step,
            startTime: nowStopwatch
        );
        // Expiry is measured on the engine clock (same instant as the UTC deadline in production).
        effect.ExpiresAtTicks = ToClockDeadline(nowStopwatch, expiresAtUtc - DateTime.UtcNow);

        if (cycleRate.Unit == CycleUnit.Ticks)
        {
            effect.NextExecuteFrame = Frame + Math.Max(1, cycleRate.Value);
            effect.NextExecuteTimeTicks = 0;
        }
        else
        {
            effect.NextExecuteTimeTicks = nowStopwatch + ToStopwatchTickInterval(cycleRate);
            effect.NextExecuteFrame = 0;
        }

        AddEffect(effect);
        return id;
    }

    public TaskIdentifier ScheduleOnce(TimeSpan delay, Action action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        var now = _clock.NowTicks;
        var effect = DynamicEffectTask.OneShot(new TaskIdentifier("Once_" + Guid.NewGuid()), action);
        effect.NextExecuteTimeTicks = ToClockDeadline(now, delay);
        AddEffect(effect);
        return effect.Id;
    }

    public TaskIdentifier ScheduleAtFrame(long frame, Action action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

        var effect = DynamicEffectTask.OneShot(new TaskIdentifier("AtFrame_" + Guid.NewGuid()), action, atFrame: true);
        effect.NextExecuteFrame = frame;
        AddEffect(effect);
        return effect.Id;
    }

    public bool CancelEffect(TaskIdentifier id) => _effects.TryRemove(id, out _);

    private void AddEffect(DynamicEffectTask effect)
    {
        _effects[effect.Id] = effect;
        _pendingEffects.Enqueue(effect);
    }

    private static long ToClockDeadline(long nowTicks, TimeSpan delay)
    {
        var seconds = delay.TotalSeconds;
        if (seconds <= 0)
            return nowTicks;
        var ticks = seconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue - (double)nowTicks ? long.MaxValue : nowTicks + (long)ticks;
    }

    // ---------------- Dynamic tasks ----------------

    public void SendTask(TaskIdentifier id, Delegate taskDelegate)
    {
        if (taskDelegate is null)
            throw new ArgumentNullException(nameof(taskDelegate));

        _dynamic.AddOrUpdate(
            id,
            _ => new DynamicTaskState { Delegate = taskDelegate, Pending = 1, InFlight = null },
            (_, state) =>
            {
                state.Delegate = taskDelegate;              // latest delegate wins
                Interlocked.Increment(ref state.Pending);   // never drop triggers
                return state;
            });
    }

    // ---------------- Cron (unchanged, fire-and-forget) ----------------

    public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
    {
        var cron = CronExpression.Parse(cronExpression);

        async void ScheduleNextRun()
        {
            var now = DateTime.UtcNow;
            var nextRunTime = cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
            if (!nextRunTime.HasValue)
                return;

            var delay = nextRunTime.Value - now;
            await Task.Delay(delay).ConfigureAwait(false);

            jobDelegate.DynamicInvoke();
            ScheduleNextRun();
        }

        ScheduleNextRun();
    }

    // ---------------- Start/Stop ----------------

    public void Start(CancellationToken token)
    {
        if (Enabled)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _linkedCts?.Dispose();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        // Captured once: Stop() disposes and clears the sources while the thread may still run.
        var runToken = _linkedCts.Token;

        Logger.LogInformation("🚀 Starting engine...");

        // Worker mode: the world steps on its own task. Inline mode: on the engine loop (RunFrame).
        if (WorldStep == WorldStepMode.Worker)
            _ = Task.Run(() => RunPhysicsWorkerAsync(runToken), runToken);

        _engineThread = new Thread(() =>
        {
            Thread.CurrentThread.Name = "EngineThread";

            while (!runToken.IsCancellationRequested)
            {
                if (_appStatus.Status != ReadyState.Alive)
                {
                    // Started before the server reported alive (or it went down again): wait for it.
                    try
                    { Task.Delay(50, runToken).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                try
                {
                    RunEngineLoop(runToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // Every task is guarded individually, so this is an engine bug; keep ticking.
                    Logger.LogError(ex, "Engine loop failed; restarting it.");
                    try
                    { Task.Delay(100, runToken).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }
                return;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _engineThread.Start();
        Enable();
        Logger.LogInformation($"⚡⚡ [ENGINE {_engineHz}Hz, world-step {WorldStep.ToString().ToLowerInvariant()}] Unleashed — powerful, fast, and breaking speed limits!");
    }

    public void Stop()
    {
        Disable();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _linkedCts?.Dispose();
        _linkedCts = null;
    }

    // ---------------- Core loops ----------------

    /// <summary>
    /// The frame loop. It runs on the engine thread itself: every frame phase (next-tick queue,
    /// cycles, dynamic tasks, effects and, inline, the world step) executes on that one thread,
    /// strictly in sequence.
    /// </summary>
    private void RunEngineLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(EngineTickPeriod);

        long lastTickStopwatch = _clock.NowTicks;

        try
        {
            while (WaitForTimer(timer, token))
            {
                if (!Enabled)
                    continue;

                long nowStopwatch = _clock.NowTicks;
                long elapsedStopwatch = nowStopwatch - lastTickStopwatch;
                lastTickStopwatch = nowStopwatch;

                RunFrame(nowStopwatch, FrameTime.TicksToDeltaSeconds(elapsedStopwatch));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private static bool WaitForTimer(PeriodicTimer timer, CancellationToken token)
    {
        var wait = timer.WaitForNextTickAsync(token);
        return wait.IsCompletedSuccessfully ? wait.Result : wait.AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// One engine frame: next-tick queue → <c>[Cycle]</c> tasks → dynamic tasks → effects and timers
    /// → world step (inline mode; worker mode hands the frame time to the world worker).
    /// Called by the engine loop, or by <see cref="EngineTestDriver"/> on a stopped engine.
    /// </summary>
    internal void RunFrame(long nowStopwatch, float dt)
    {
        CurrentTick = Interlocked.Increment(ref _frame);

        AdoptPendingStaticTasks();
        RunNextTickQueue();
        RunStaticTasks(nowStopwatch);
        StartDynamicTasksBudgeted();
        RunEffects(nowStopwatch, dt);

        if (WorldStep == WorldStepMode.Inline)
        {
            try
            {
                _worldCoordinator.Step(dt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportFault("world step", ex);
            }
            return;
        }

        lock (_physicsDtLock)
            _pendingPhysicsDt += dt;
        _physicsTicks.Writer.TryWrite(true);
    }

    private async Task RunPhysicsWorkerAsync(CancellationToken token)
    {
        try
        {
            while (await _physicsTicks.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_physicsTicks.Reader.TryRead(out _)) { }

                float dt;
                lock (_physicsDtLock)
                {
                    dt = (float)_pendingPhysicsDt;
                    _pendingPhysicsDt = 0;
                }

                try
                {
                    _worldCoordinator.Step(dt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Don't die — keep the loop running
                    ReportFault("world step", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    // ---------------- Tick subroutines ----------------

    private void RunNextTickQueue()
    {
        while (_nextTickQueue.TryDequeue(out var del))
        {
            // One throwing delegate must not stop the loop or the delegates queued after it.
            // Async delegates are awaited in order (the queue is sequential), on the engine thread.
            try
            {
                if (del is Action action)
                    action();
                else
                    ExecuteDelegateAsync(del).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ReportFault("next-tick task " + DescribeDelegate(del), ex);
            }
        }
    }

    private void AdoptPendingStaticTasks()
    {
        while (_pendingStaticTasks.TryDequeue(out var task))
            _staticTasks.Add(task);
    }

    private void RunStaticTasks(long nowStopwatch)
    {
        foreach (var task in _staticTasks)
        {
            bool due;

            long frame = _frame;
            long interval = 0;
            long lastRun = 0;
            if (task.CycleRate.Unit == CycleUnit.Ticks)
            {
                _staticLastRunFrame.TryGetValue(task.Id, out var lastFrame);
                due = lastFrame == 0 || (frame - lastFrame) >= Math.Max(1, task.CycleRate.Value);
            }
            else
            {
                interval = ToStopwatchTickInterval(task.CycleRate);
                _staticLastRunStopwatch.TryGetValue(task.Id, out lastRun);
                due = lastRun == 0 || (nowStopwatch - lastRun) >= interval;
            }

            if (!due)
                continue;

            if (_staticInFlight.TryGetValue(task.Id, out var running) && !running.IsCompleted)
                continue;

            if (task.CycleRate.Unit == CycleUnit.Ticks)
                _staticLastRunFrame[task.Id] = frame;
            else
                _staticLastRunStopwatch[task.Id] = NextCadence(lastRun, interval, nowStopwatch);

            _staticInFlight[task.Id] = ExecuteDelegateAsync(task.Delegate);
        }

        if (_staticInFlight.Count > 0)
        {
            List<TaskIdentifier>? done = null;
            foreach (var kv in _staticInFlight)
            {
                if (!kv.Value.IsCompleted)
                    continue;
                if (kv.Value.IsFaulted)
                    ReportFault("cycle task " + kv.Key.Id, kv.Value.Exception!.GetBaseException());
                (done ??= new List<TaskIdentifier>()).Add(kv.Key);
            }
            if (done is not null)
                foreach (var id in done)
                    _staticInFlight.Remove(id);
        }
    }

    private void RunEffects(long nowStopwatch, float dt)
    {
        while (_pendingEffects.TryDequeue(out var added))
            _effectOrder.Add(added);

        if (_effectOrder.Count == 0)
            return;

        long frame = _frame;
        var removed = 0;
        for (var i = 0; i < _effectOrder.Count; i++)
        {
            var effect = _effectOrder[i];
            var id = effect.Id;

            // Cancelled (or replaced) since it was scheduled.
            if (!_effects.TryGetValue(id, out var live) || !ReferenceEquals(live, effect))
            {
                _effectOrder[i] = null!;
                removed++;
                continue;
            }

            if (!effect.IsOneShot && nowStopwatch >= effect.ExpiresAtTicks)
            {
                _effects.TryRemove(id, out _);
                _effectOrder[i] = null!;
                removed++;
                continue;
            }

            bool due = effect.IsFrameBased
                ? frame >= effect.NextExecuteFrame
                : nowStopwatch >= effect.NextExecuteTimeTicks;

            if (!due)
                continue;

            try
            {
                if (effect.IsOneShot)
                {
                    _effects.TryRemove(id, out _);
                    _effectOrder[i] = null!;
                    removed++;
                    effect.Once!();
                    continue;
                }

                effect.Step(dt);

                if (effect.Rate.Unit == CycleUnit.Ticks)
                    effect.NextExecuteFrame = frame + Math.Max(1, effect.Rate.Value);
                else
                {
                    // One interval after the previous due time (no drift), or after now when
                    // more than an interval behind (no burst of catch-up runs).
                    long interval = ToStopwatchTickInterval(effect.Rate);
                    long next = effect.NextExecuteTimeTicks + interval;
                    effect.NextExecuteTimeTicks = nowStopwatch >= next ? nowStopwatch + interval : next;
                }
            }
            catch (Exception ex)
            {
                if (_effects.TryRemove(id, out _) && _effectOrder[i] is not null)
                {
                    _effectOrder[i] = null!;
                    removed++;
                }
                ReportFault("effect " + id.Id, ex);
            }
        }

        if (removed > 0)
            _effectOrder.RemoveAll(static e => e is null);
    }

    /// <summary>
    /// The last-run mark after a time-based run: one interval after the previous mark, so the
    /// average rate is the configured one even when frames do not line up with the interval;
    /// resynchronized to now after a stall of two or more intervals (no burst of catch-up runs).
    /// </summary>
    private static long NextCadence(long lastRun, long interval, long now)
    {
        if (lastRun <= 0 || interval <= 0)
            return now;
        var next = lastRun + interval;
        return now - next >= interval ? now : next;
    }

    private void StartDynamicTasksBudgeted()
    {
        int started = 0;

        foreach (var kv in _dynamic)
        {
            if (started >= MaxDynamicStartsPerTick)
                break;

            var id = kv.Key;
            var state = kv.Value;

            var inFlight = Volatile.Read(ref state.InFlight);
            if (inFlight != null && !inFlight.IsCompleted)
                continue;

            if (Volatile.Read(ref state.Pending) <= 0)
            {
                _dynamic.TryRemove(id, out _);
                continue;
            }

            if (!TryClaimOnePending(state))
                continue;

            var task = ExecuteDelegateAsync(state.Delegate);
            Volatile.Write(ref state.InFlight, task);
            started++;
        }
    }

    private static bool TryClaimOnePending(DynamicTaskState state)
    {
        while (true)
        {
            int current = Volatile.Read(ref state.Pending);
            if (current <= 0)
                return false;

            if (Interlocked.CompareExchange(ref state.Pending, current - 1, current) == current)
                return true;
        }
    }

    // ---------------- Static task scheduling (startup-time) ----------------

    public void ScheduleTask(Delegate taskDelegate, CycleRate? rate = null)
    {
        // No rate = every frame (a plain [Cycle]). It used to fall back to the engine's own rate,
        // which with unit "ticks" meant every framerateHz frames, i.e. once per second.
        var actualRate = rate ?? EveryFrame;

        if (actualRate.Unit == CycleUnit.Hz && actualRate.Value > _engineHz)
            throw new ArgumentException($"Frequency {actualRate} must be <= engine frequency {_engineHz}Hz.", nameof(rate));

        var methodInfo = taskDelegate.Method;
        var parameters = methodInfo.GetParameters();

        Delegate runnable;
        if (parameters.Length == 0 && taskDelegate is Func<Task> or Action)
        {
            // Parameterless: invoke directly (no reflection per frame), and keep the returned
            // Task so an async [Cycle] is tracked as in flight and never overlaps itself.
            runnable = taskDelegate;
        }
        else
        {
            var resolvedParameters = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;
                resolvedParameters[i] = _serviceProvider.GetService(paramType)
                    ?? throw new InvalidOperationException(
                        $"Cannot resolve dependency of type {paramType.FullName} for method {methodInfo.Name}.");
            }

            // No cache. CreateTaskDelegate wraps `void` methods in a shared closure
            // (same lambda source line → same compiler-generated method), so caching
            // by methodInfo+paramTypes collapses all void [Cycle]s to one key.
            // The first void registration would then own the cached precompiled,
            // and every later void [Cycle] would silently invoke the first one
            // forever — i.e. only one of N void [Cycle] handlers would ever run.
            // ScheduleTask is called once per handler at startup; the cost of
            // building a fresh wrapper here is negligible.
            runnable = CreateDelegateWithResolvedParameters(taskDelegate, resolvedParameters);
        }

        _pendingStaticTasks.Enqueue(new EngineStaticTask(runnable, actualRate, Stopwatch.GetTimestamp()));
    }

    private static Func<Task> CreateDelegateWithResolvedParameters(Delegate taskDelegate, object[] resolvedParameters)
        => () =>
        {
            try
            {
                return taskDelegate.DynamicInvoke(resolvedParameters) as Task ?? Task.CompletedTask;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                return Task.FromException(ex.InnerException);
            }
        };

    // ---------------- Delegate runner ----------------

    private static async Task ExecuteDelegateAsync(Delegate del)
    {
        switch (del)
        {
            case Func<Task> f:
                await f().ConfigureAwait(false);
                break;
            case Action a:
                a();
                break;
            default:
                try
                {
                    if (del.DynamicInvoke() is Task t)
                        await t.ConfigureAwait(false);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                }
                break;
        }
    }

    private static string DescribeDelegate(Delegate del) =>
        $"{del.Method.DeclaringType?.Name ?? "?"}.{del.Method.Name}";

    /// <summary>
    /// Logs a task failure without stopping the loop. Repeats of the same source are
    /// summarized at most every 10 s so a task that throws every frame cannot flood the log.
    /// </summary>
    private void ReportFault(string source, Exception ex)
    {
        var entry = _faults.GetOrAdd(source, static _ => new FaultLog());
        var now = Stopwatch.GetTimestamp();
        long suppressed;
        lock (entry)
        {
            if (entry.LastLogged != 0 && now - entry.LastLogged < FaultLogInterval)
            {
                entry.Suppressed++;
                return;
            }
            suppressed = entry.Suppressed;
            entry.Suppressed = 0;
            entry.LastLogged = now;
        }
        if (suppressed > 0)
            Logger.LogError(ex, "Engine {Source} threw (and {Count} more times since the last report); the engine keeps running.", source, suppressed);
        else
            Logger.LogError(ex, "Engine {Source} threw; the engine keeps running.", source);
    }

    // ---------------- Timing helpers ----------------

    private static long ToStopwatchTickInterval(CycleRate rate)
    {
        return rate.Unit switch
        {
            CycleUnit.Hz => Math.Max(1, Stopwatch.Frequency / Math.Max(1, rate.Value)),
            // CycleRate stores Seconds/Milliseconds rates as the cycle period in TimeSpan ticks
            // (100 ns): [Cycle(30, CycleUnit.Seconds)] = 30 times per second.
            CycleUnit.Milliseconds or CycleUnit.Seconds => Math.Max(1, (long)((double)Math.Max(1, rate.Value) * Stopwatch.Frequency / TimeSpan.TicksPerSecond)),

            // IMPORTANT: Ticks are frames; do not convert to stopwatch ticks here.
            CycleUnit.Ticks => throw new InvalidOperationException("CycleUnit.Ticks represents frames; use frame-based scheduling."),

            _ => Math.Max(1, rate.Value)
        };
    }
}
