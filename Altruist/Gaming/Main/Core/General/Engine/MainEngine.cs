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
    private readonly IServiceProvider _serviceProvider;
    private readonly LinkedList<EngineStaticTask> _staticTasks;
    private readonly ConcurrentDictionary<TaskIdentifier, Task> _scheduledDynamicTasks = new();
    private readonly ConcurrentDictionary<TaskIdentifier, Delegate> _dynamicTasks;
    private readonly Dictionary<string, Action> _precompiledDelegatesCache = new();

    private readonly ConcurrentQueue<Delegate> _nextTickTasks = new();

    private readonly ConcurrentDictionary<TaskIdentifier, DynamicEffectTask> _effectTasks =
        new ConcurrentDictionary<TaskIdentifier, DynamicEffectTask>();

    private CancellationTokenSource _cancellationTokenSource;
    private readonly CycleRate _engineRate;
    private Thread? _engineThread;

    public CycleRate Rate => _engineRate;

    public bool Enabled { get; private set; }

    public int Throttle = 1000000;

    private IServerStatus _appStatus;

    private readonly IGameWorldOrganizer _worldCoordinator;

    private readonly Channel<float> _physicsTicks = Channel.CreateBounded<float>(
    new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public AltruistEngine(
        IServerStatus serverStatus,
        IServiceProvider serviceProvider,
        IGameWorldOrganizer worldCoordinator,
        [AppConfigValue("altruist:game:engine:framerateHz", "30")]
        int engineFrequencyHz = 30,
        [AppConfigValue("altruist:game:engine:unit")]
        CycleUnit unit = CycleUnit.Ticks,
        [AppConfigValue("altruist:game:engine:throttle")]
        int? throttle = null)
    {
        _staticTasks = new LinkedList<EngineStaticTask>();
        _dynamicTasks = new ConcurrentDictionary<TaskIdentifier, Delegate>();
        _engineRate = new CycleRate(engineFrequencyHz, unit);
        Throttle = throttle ?? (int)(1_000_000_000 / (_engineRate.Value + 1));
        _cancellationTokenSource = new CancellationTokenSource();
        _serviceProvider = serviceProvider;
        _appStatus = serverStatus;
        _worldCoordinator = worldCoordinator;
    }

    public void Enable()
    {
        Enabled = true;
    }

    public void Disable()
    {
        Enabled = false;
    }

    public void SyncCommit(Action commit)
    {
        if (commit == null)
            throw new ArgumentNullException(nameof(commit));
        WaitForNextTick(commit);
    }

    public Task<T> SyncCommit<T>(Func<T> commit)
    {
        if (commit == null)
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

    public void WaitForNextTick(Delegate task)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));
        _nextTickTasks.Enqueue(task);
    }

    public void WaitForNextTick(Action task)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));
        EnqueueNextTick(task);
    }

    private void EnqueueNextTick(Delegate task)
    {
        _nextTickTasks.Enqueue(task);
    }

    public void WaitForNextTick(Func<Task> task)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));
        EnqueueNextTick(task);
    }

    public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
    {
        var cron = CronExpression.Parse(cronExpression);

        async void ScheduleNextRun()
        {
            var now = DateTime.UtcNow;
            var nextRunTime = cron.GetNextOccurrence(now, TimeZoneInfo.Utc);

            if (nextRunTime.HasValue)
            {
                var delay = nextRunTime.Value - now;
                await Task.Delay(delay);
                jobDelegate.DynamicInvoke();

                ScheduleNextRun();
            }
        }

        ScheduleNextRun();
    }

    public TaskIdentifier ScheduleEffect(
        CycleRate cycleRate,
        DateTime expiresAtUtc,
        Action<float> step)
    {
        var id = new TaskIdentifier("Effect_" + Guid.NewGuid());
        long now = FrameTime.NowTicks;

        var task = new DynamicEffectTask(
            id: id,
            rate: cycleRate,
            expiresAtUtc: expiresAtUtc,
            step: step,
            startTime: now
        );

        _effectTasks[id] = task;
        return id;
    }

    public bool CancelEffect(TaskIdentifier id)
    {
        return _effectTasks.TryRemove(id, out _);
    }

    public void SendTask(TaskIdentifier taskId, Delegate taskDelegate)
    {
        var existing = _scheduledDynamicTasks.TryGetValue(taskId, out var existingTask);
        if (existingTask != null && !existingTask.IsCompleted)
        {
            // TODO: Currently, if a task is triggered while an existing one with the same ID is still running,
            // the new trigger is ignored. This can lead to missed executions if the task isn't scheduled again.
            //
            // Consider implementing a trigger queue or flag-based requeue system:
            // - If a task is already running, queue a "pending" flag or count.
            // - After the running task completes, check the queue and re-schedule the task if it was triggered again.
            //
            // This ensures high-frequency tasks are never silently dropped and are executed at least once per trigger.
            return;
        }

        _scheduledDynamicTasks.TryRemove(taskId, out _);
        _dynamicTasks.AddOrUpdate(taskId, taskDelegate, (_, _) => taskDelegate);
    }

    public void Start(CancellationToken token)
    {
        if (Enabled)
            return;

        _cancellationTokenSource = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _cancellationTokenSource.Token);

        _ = Task.Run(() => RunPhysicsWorkerAsync(linkedCts.Token), linkedCts.Token);

        _engineThread = new Thread(() =>
        {
            Thread.CurrentThread.Name = "EngineThread";

            while (!linkedCts.IsCancellationRequested)
            {
                if (_appStatus.Status == ReadyState.Alive)
                {
                    try
                    {
                        RunEngineLoopAsync(linkedCts.Token).GetAwaiter().GetResult();
                    }
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

    private async Task RunPhysicsWorkerAsync(CancellationToken token)
    {
        try
        {
            while (await _physicsTicks.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                float dt = 0f;
                while (_physicsTicks.Reader.TryRead(out var v))
                    dt = v;

                await _worldCoordinator.StepAsync(dt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    // Pre-sized buffer for dynamic tasks
    private readonly Task[] _taskBuffer = new Task[128];
    private int _taskCount = 0;

    private async Task RunEngineLoopAsync(CancellationToken token)
    {
        // If _engineRate.Value is Hz:
        var period = TimeSpan.FromSeconds(1.0 / _engineRate.Value);
        using var timer = new PeriodicTimer(period);

        var staticInFlight = new Dictionary<TaskIdentifier, Task>();
        var staticLastRunTicks = new Dictionary<TaskIdentifier, long>();

        long lastTick = FrameTime.NowTicks;

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!Enabled)
                    continue;

                CurrentTick++;

                long nowTicks = FrameTime.NowTicks;
                long elapsedTicks = nowTicks - lastTick;
                lastTick = nowTicks;

                float dt = FrameTime.TicksToDeltaSeconds(elapsedTicks);

                // 1) Next-tick tasks
                while (_nextTickTasks.TryDequeue(out var nextTickDelegate))
                {
                    await ExecuteTaskAsync(nextTickDelegate).ConfigureAwait(false);
                }

                // 2) Static tasks (same logic you had, but safe)
                foreach (var task in _staticTasks)
                {
                    staticLastRunTicks.TryGetValue(task.Id, out var lastRun);
                    bool due = lastRun == 0 || (nowTicks - lastRun) >= task.CycleRate.Value;
                    if (!due)
                        continue;

                    if (!staticInFlight.TryGetValue(task.Id, out var running) || running.IsCompleted)
                    {
                        staticLastRunTicks[task.Id] = nowTicks;
                        staticInFlight[task.Id] = ExecuteTaskAsync(task.Delegate);
                    }
                }

                if (staticInFlight.Count > 50)
                {
                    var toRemove = new List<TaskIdentifier>();
                    foreach (var kv in staticInFlight)
                        if (kv.Value.IsCompleted)
                            toRemove.Add(kv.Key);

                    foreach (var id in toRemove)
                        staticInFlight.Remove(id);
                }

                // 3) Dynamic tasks
                if (_dynamicTasks.Count > 0)
                {
                    _taskCount = 0;

                    foreach (var (key, task) in _dynamicTasks)
                    {
                        _scheduledDynamicTasks.TryGetValue(key, out var existingTask);
                        if (existingTask != null && !existingTask.IsCompleted)
                            continue;

                        _dynamicTasks.TryRemove(key, out _);
                        _scheduledDynamicTasks.TryRemove(key, out _);

                        var asyncTask = ExecuteTaskAsync(task);

                        if (_taskCount < _taskBuffer.Length)
                        {
                            _taskBuffer[_taskCount++] = asyncTask;
                            _scheduledDynamicTasks[key] = asyncTask;
                        }
                        else
                        {
                            await asyncTask.ConfigureAwait(false);
                        }
                    }
                }

                // 4) Effects (dt is correct now)
                if (_effectTasks.Count > 0)
                {
                    var nowUtc = DateTime.UtcNow;

                    foreach (var (id, effect) in _effectTasks)
                    {
                        if (nowUtc >= effect.ExpiresAtUtc)
                        {
                            _effectTasks.TryRemove(id, out _);
                            continue;
                        }

                        if (nowTicks < effect.NextExecuteTimeTicks)
                            continue;

                        try
                        {
                            effect.Step(dt);
                            effect.NextExecuteTimeTicks = nowTicks + effect.Rate.Value;
                        }
                        catch
                        {
                            _effectTasks.TryRemove(id, out _);
                        }
                    }
                }

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

    /// <summary>
    /// Schedules a task to be executed at a specific frequency. The task is resolved and its dependencies
    /// are injected from the provided service provider. If the task has been scheduled before with the same
    /// method signature and parameters, the previously created delegate will be reused to avoid redundant work.
    /// </summary>
    /// <param name="taskDelegate">The delegate representing the task to be scheduled.</param>
    /// <param name="rate">The cycle rate (frequency) at which the task should be executed. If not specified,
    /// it defaults to the engine's rate. The frequency must not exceed the engine's frequency rate.</param>
    /// <exception cref="ArgumentException">Thrown if the provided rate exceeds the engine's rate.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a required dependency cannot be resolved or if
    /// a precompiled delegate cannot be created.</exception>
    public void ScheduleTask(Delegate taskDelegate, CycleRate? rate = null)
    {
        var actualFrequency = rate ?? _engineRate;

        if (actualFrequency.Value > _engineRate.Value)
        {
            throw new ArgumentException($"Frequency {actualFrequency} must be less than or equal to the engine frequency ${_engineRate}.", nameof(actualFrequency));
        }

        var methodInfo = taskDelegate.Method;
        var parameters = methodInfo.GetParameters();
        var resolvedParameters = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var paramType = parameters[i].ParameterType;
            resolvedParameters[i] = _serviceProvider.GetService(paramType)
                                    ?? throw new InvalidOperationException($"Cannot resolve dependency of type {paramType.FullName} for method {methodInfo.Name}.");
        }

        var cacheKey = GenerateCacheKey(methodInfo, resolvedParameters);
        if (!_precompiledDelegatesCache.TryGetValue(cacheKey, out var precompiledDelegate))
        {
            precompiledDelegate = CreateDelegateWithResolvedParameters(taskDelegate, resolvedParameters);
            _precompiledDelegatesCache[cacheKey] = precompiledDelegate;
        }

        _staticTasks.AddLast(new EngineStaticTask(precompiledDelegate, actualFrequency, Stopwatch.GetTimestamp()));
    }

    private string GenerateCacheKey(MethodInfo methodInfo, object[] resolvedParameters)
    {
        var paramTypes = string.Join(",", resolvedParameters.Select(p => p?.GetType().FullName));
        return $"{methodInfo.DeclaringType!.FullName}.{methodInfo.Name}({paramTypes})";
    }

    private Action CreateDelegateWithResolvedParameters(Delegate taskDelegate, object[] resolvedParameters)
    {
        return () =>
        {
            taskDelegate.DynamicInvoke(resolvedParameters);
        };
    }

    private async Task ExecuteTaskAsync(Delegate taskDelegate)
    {
        if (taskDelegate is Func<Task> asyncDelegate)
        {
            await asyncDelegate();
        }
        else if (taskDelegate is Action syncDelegate)
        {
            syncDelegate();
        }
    }

    public void Stop()
    {
        Disable();
        _cancellationTokenSource?.Cancel();
    }

    private static long ToStopwatchTickInterval(CycleRate rate)
    {
        // Assumes FrameTime.NowTicks uses Stopwatch.GetTimestamp() (same tick base).
        // Adjust if your FrameTime uses a different clock.
        return rate.Unit switch
        {
            CycleUnit.Hz => Math.Max(1, Stopwatch.Frequency / rate.Value),
            CycleUnit.Ticks => Math.Max(1, rate.Value),
            CycleUnit.Milliseconds => Math.Max(1, Stopwatch.Frequency * rate.Value / 1000),
            CycleUnit.Seconds => Math.Max(1, Stopwatch.Frequency * rate.Value),
            _ => Math.Max(1, rate.Value)
        };
    }

    private static TimeSpan ToPeriod(CycleRate rate)
    {
        long tickInterval = ToStopwatchTickInterval(rate);
        return TimeSpan.FromSeconds((double)tickInterval / Stopwatch.Frequency);
    }

}
