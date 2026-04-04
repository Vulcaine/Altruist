using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

using Altruist.Gaming;

using Cronos;

namespace Altruist.Engine;

[Service(typeof(IEngineCore))]
[ConditionalOnConfig("altruist:game:engine")]
public class AltruistEngine : IAltruistEngine
{
    public static long CurrentTick { get; private set; } = 0;

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

    private CancellationTokenSource _cts = new();
    private Thread? _engineThread;

    // -------- Next-tick (run once next tick, sequential) --------
    private readonly ConcurrentQueue<Delegate> _nextTickQueue = new();

    // -------- Physics dt (latest only) --------
    private readonly Channel<float> _physicsTicks = Channel.CreateBounded<float>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    // -------- Static tasks (registered once, run on cadence, never overlap per task) --------
    private readonly List<EngineStaticTask> _staticTasks = new();
    private readonly Dictionary<TaskIdentifier, long> _staticLastRunStopwatch = new(); // for time-based
    private readonly Dictionary<TaskIdentifier, long> _staticLastRunFrame = new();     // for frame-based
    private readonly Dictionary<TaskIdentifier, Task> _staticInFlight = new();

    // Cache only for building the static delegates (startup-time).
    private readonly Dictionary<string, Action> _staticDelegateCache = new();

    // -------- Effects --------
    private readonly ConcurrentDictionary<TaskIdentifier, DynamicEffectTask> _effects = new();

    // -------- Dynamic tasks (requested at runtime; each request must run at least once; never overlap per id) --------
    private sealed class DynamicTaskState
    {
        public Delegate Delegate = default!;
        public int Pending;               // executions owed
        public Task? InFlight;            // currently running
    }

    private readonly ConcurrentDictionary<TaskIdentifier, DynamicTaskState> _dynamic = new();
    private const int MaxDynamicStartsPerTick = 128;

    public AltruistEngine(
        IServerStatus serverStatus,
        IServiceProvider serviceProvider,
        IGameWorldOrganizer worldCoordinator,
        [AppConfigValue("altruist:game:engine:framerateHz", "30")] int engineFrequencyHz = 30,
        [AppConfigValue("altruist:game:engine:unit")] CycleUnit unit = CycleUnit.Ticks,
        [AppConfigValue("altruist:game:engine:throttle")] int? throttle = null)
    {
        _serviceProvider = serviceProvider;
        _appStatus = serverStatus;
        _worldCoordinator = worldCoordinator;

        _engineHz = Math.Max(1, engineFrequencyHz);
        _engineRate = new CycleRate(engineFrequencyHz, unit); // visibility/config only

        Throttle = throttle ?? (int)(1_000_000_000 / (_engineRate.Value + 1));
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;

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

        long nowStopwatch = FrameTime.NowTicks;

        var effect = new DynamicEffectTask(
            id: id,
            rate: cycleRate,
            expiresAtUtc: expiresAtUtc,
            step: step,
            startTime: nowStopwatch
        );

        if (cycleRate.Unit == CycleUnit.Ticks)
        {
            effect.NextExecuteFrame = CurrentTick + Math.Max(1, cycleRate.Value);
            effect.NextExecuteTimeTicks = 0;
        }
        else
        {
            effect.NextExecuteTimeTicks = nowStopwatch + ToStopwatchTickInterval(cycleRate);
            effect.NextExecuteFrame = 0;
        }

        _effects[id] = effect;
        return id;
    }

    public bool CancelEffect(TaskIdentifier id) => _effects.TryRemove(id, out _);

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

        _cts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);

        // Physics worker runs as a Task
        _ = Task.Run(() => RunPhysicsWorkerAsync(linkedCts.Token), linkedCts.Token);

        _engineThread = new Thread(() =>
        {
            Thread.CurrentThread.Name = "EngineThread";

            while (!linkedCts.IsCancellationRequested)
            {
                if (_appStatus.Status == ReadyState.Alive)
                {
                    try
                    { RunEngineLoopAsync(linkedCts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { }
                    catch { /* log */ }
                    return;
                }

                Thread.Sleep(5000);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _engineThread.Start();
        Enable();
    }

    public void Stop()
    {
        Disable();
        _cts.Cancel();
    }

    // ---------------- Core loops ----------------

    private async Task RunEngineLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(EngineTickPeriod);

        long lastTickStopwatch = FrameTime.NowTicks;

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!Enabled)
                    continue;

                CurrentTick++;

                long nowStopwatch = FrameTime.NowTicks;
                long elapsedStopwatch = nowStopwatch - lastTickStopwatch;
                lastTickStopwatch = nowStopwatch;

                float dt = FrameTime.TicksToDeltaSeconds(elapsedStopwatch);

                await RunNextTickQueueAsync().ConfigureAwait(false);
                RunStaticTasks(nowStopwatch);
                StartDynamicTasksBudgeted();
                RunEffects(nowStopwatch, dt);

                _physicsTicks.Writer.TryWrite(dt);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _physicsTicks.Writer.TryComplete();
        }
    }

    private async Task RunPhysicsWorkerAsync(CancellationToken token)
    {
        try
        {
            while (await _physicsTicks.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                float dt = 0f;
                while (_physicsTicks.Reader.TryRead(out var v))
                    dt = v;

                _worldCoordinator.Step(dt);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    // ---------------- Tick subroutines ----------------

    private async Task RunNextTickQueueAsync()
    {
        while (_nextTickQueue.TryDequeue(out var del))
            await ExecuteDelegateAsync(del).ConfigureAwait(false);
    }

    private void RunStaticTasks(long nowStopwatch)
    {
        foreach (var task in _staticTasks)
        {
            bool due;

            if (task.CycleRate.Unit == CycleUnit.Ticks)
            {
                _staticLastRunFrame.TryGetValue(task.Id, out var lastFrame);
                due = lastFrame == 0 || (CurrentTick - lastFrame) >= Math.Max(1, task.CycleRate.Value);
            }
            else
            {
                long interval = ToStopwatchTickInterval(task.CycleRate);
                _staticLastRunStopwatch.TryGetValue(task.Id, out var lastRun);
                due = lastRun == 0 || (nowStopwatch - lastRun) >= interval;
            }

            if (!due)
                continue;

            if (_staticInFlight.TryGetValue(task.Id, out var running) && !running.IsCompleted)
                continue;

            if (task.CycleRate.Unit == CycleUnit.Ticks)
                _staticLastRunFrame[task.Id] = CurrentTick;
            else
                _staticLastRunStopwatch[task.Id] = nowStopwatch;

            _staticInFlight[task.Id] = ExecuteDelegateAsync(task.Delegate);
        }

        if (_staticInFlight.Count > 0)
        {
            var done = new List<TaskIdentifier>();
            foreach (var kv in _staticInFlight)
                if (kv.Value.IsCompleted)
                    done.Add(kv.Key);
            foreach (var id in done)
                _staticInFlight.Remove(id);
        }
    }

    private void RunEffects(long nowStopwatch, float dt)
    {
        if (_effects.IsEmpty)
            return;

        var nowUtc = DateTime.UtcNow;

        foreach (var (id, effect) in _effects)
        {
            if (nowUtc >= effect.ExpiresAtUtc)
            {
                _effects.TryRemove(id, out _);
                continue;
            }

            bool due = effect.Rate.Unit switch
            {
                CycleUnit.Ticks => CurrentTick >= effect.NextExecuteFrame,
                _ => nowStopwatch >= effect.NextExecuteTimeTicks
            };

            if (!due)
                continue;

            try
            {
                effect.Step(dt);

                if (effect.Rate.Unit == CycleUnit.Ticks)
                    effect.NextExecuteFrame = CurrentTick + Math.Max(1, effect.Rate.Value);
                else
                    effect.NextExecuteTimeTicks = nowStopwatch + ToStopwatchTickInterval(effect.Rate);
            }
            catch
            {
                _effects.TryRemove(id, out _);
            }
        }
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
        var actualRate = rate ?? _engineRate;

        if (actualRate.Unit == CycleUnit.Hz && actualRate.Value > _engineHz)
            throw new ArgumentException($"Frequency {actualRate} must be <= engine frequency {_engineHz}Hz.", nameof(rate));

        var methodInfo = taskDelegate.Method;
        var parameters = methodInfo.GetParameters();

        var resolvedParameters = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var paramType = parameters[i].ParameterType;
            resolvedParameters[i] = _serviceProvider.GetService(paramType)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve dependency of type {paramType.FullName} for method {methodInfo.Name}.");
        }

        var cacheKey = GenerateCacheKey(methodInfo, resolvedParameters);

        if (!_staticDelegateCache.TryGetValue(cacheKey, out var precompiled))
        {
            precompiled = CreateDelegateWithResolvedParameters(taskDelegate, resolvedParameters);
            _staticDelegateCache[cacheKey] = precompiled;
        }

        _staticTasks.Add(new EngineStaticTask(precompiled, actualRate, Stopwatch.GetTimestamp()));
    }

    private static string GenerateCacheKey(MethodInfo methodInfo, object[] resolvedParameters)
    {
        var paramTypes = string.Join(",", resolvedParameters.Select(p => p?.GetType().FullName));
        return $"{methodInfo.DeclaringType!.FullName}.{methodInfo.Name}({paramTypes})";
    }

    private static Action CreateDelegateWithResolvedParameters(Delegate taskDelegate, object[] resolvedParameters)
        => () => taskDelegate.DynamicInvoke(resolvedParameters);

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
                del.DynamicInvoke();
                break;
        }
    }

    // ---------------- Timing helpers ----------------

    private static long ToStopwatchTickInterval(CycleRate rate)
    {
        return rate.Unit switch
        {
            CycleUnit.Hz => Math.Max(1, Stopwatch.Frequency / Math.Max(1, rate.Value)),
            CycleUnit.Milliseconds => Math.Max(1, (Stopwatch.Frequency * Math.Max(1, rate.Value)) / 1000),
            CycleUnit.Seconds => Math.Max(1, Stopwatch.Frequency * Math.Max(1, rate.Value)),

            // IMPORTANT: Ticks are frames; do not convert to stopwatch ticks here.
            CycleUnit.Ticks => throw new InvalidOperationException("CycleUnit.Ticks represents frames; use frame-based scheduling."),

            _ => Math.Max(1, rate.Value)
        };
    }
}
