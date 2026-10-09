/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Engine;

/// <summary>
/// Defines common update frequencies (in Hz) for different types of game servers.
/// </summary>
public class FrameRate
{
    /// <summary>
    /// 1Hz (1 update per second).
    /// Use for extremely low-priority updates, background tasks, or turn-based games.
    /// </summary>
    public static int Hz1 = 1;

    /// <summary>
    /// 10Hz (10 updates per second).
    /// Suitable for slow-paced games, AI processing, or non-critical events in large-scale MMOs.
    /// </summary>
    public static int Hz10 = 10;

    /// <summary>
    /// 30Hz (30 updates per second).
    /// Good for real-time multiplayer in strategy games, MMORPGs, or casual games with moderate responsiveness.
    /// </summary>
    public static int Hz30 = 30;

    /// <summary>
    /// 60Hz (60 updates per second).
    /// Recommended for most action-oriented multiplayer games, including shooters, racing games, and platformers.
    /// </summary>
    public static int Hz60 = 60;

    /// <summary>
    /// 120Hz (120 updates per second).
    /// Used for high-speed, competitive games where low latency is critical (e.g., esports, racing games, FPS with high refresh rates).
    /// </summary>
    public static int Hz120 = 120;

    /// <summary>
    /// 128Hz (128 updates per second).
    /// Commonly used in professional esports FPS titles (e.g., CS:GO, Valorant) for ultra-responsive gameplay.
    /// </summary>
    public static int Hz128 = 128;

    /// <summary>
    /// 256Hz (256 updates per second).
    /// Extremely high update rate, mainly useful for advanced physics simulations, VR applications, and low-latency esports games.
    /// </summary>
    public static int Hz256 = 256;
}

/// <summary>Stopwatch-based time helpers. Prefer the engine's <see cref="IEngineClock"/> inside engine code
/// (it can be replaced by a <see cref="ManualEngineClock"/> in tests); this reads the real clock.</summary>
public static class FrameTime
{
    private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    /// <summary>Current <see cref="Stopwatch.GetTimestamp"/> (units of <see cref="Stopwatch.Frequency"/>).</summary>
    public static long NowTicks => Stopwatch.GetTimestamp();
    /// <summary>Converts a stopwatch tick span to seconds.</summary>
    public static float TicksToDeltaSeconds(long ticks) => (float)(ticks * TicksToSeconds);
}

/// <summary>
/// Finds every <c>[Cycle]</c>-annotated method on the services in the root DI provider and registers it
/// on the engine: cron expressions via <see cref="IEngineCore.RegisterCronJob"/>, rates (fixed or read
/// from config) and plain <c>[Cycle]</c> (every frame) via <see cref="IEngineCore.ScheduleTask"/>.
/// Registered automatically as a service and runs in its <c>[PostConstruct]</c>; game code does not
/// call it. Supported methods are parameterless and return <c>void</c> or <see cref="Task"/>; instance and
/// static methods both work (a static method is registered once per service type that declares it). When a
/// subclass and its base both expose the same method, the subclass' registration wins.
/// </summary>
[Service]
public class MethodScheduler
{
    private readonly IEngineCore _engine;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, (object? serviceInstance, HashSet<MethodInfo>)> _registeredMethodsByType;

    /// <summary>Created by DI.</summary>
    public MethodScheduler(IEngineCore engine, IServiceProvider serviceProvider)
    {
        _engine = engine;
        _serviceProvider = serviceProvider;
        _registeredMethodsByType = new Dictionary<Type, (object? serviceInstance, HashSet<MethodInfo>)>();
    }

    /// <summary>
    /// Registers all <c>[Cycle]</c>-annotated methods of the root provider's services on the
    /// engine. This is the only place <c>[Cycle]</c> methods are registered, so they run on the
    /// same instances whose <c>[PostConstruct]</c> hooks ran. The engine itself is started by
    /// <c>ServerStatus</c> once every connectable service is up.
    /// </summary>
    [PostConstruct]
    public void SelfRegister()
    {
        if (_registeredMethodsByType.Count > 0) return;
        var methods = RegisterMethods(_serviceProvider);

        var logger = (_serviceProvider.GetService<ILoggerFactory>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<MethodScheduler>();
        if (methods.Count == 0)
        {
            logger.LogInformation("❗Nothing to run.. 🙁 Mark something with [Cycle(Hz or cron)] to let me show my power. Please!");
            return;
        }

        var methodsDisplay = string.Join("\n", methods.Select(m =>
        {
            var frequency = m.GetCustomAttribute<CycleAttribute>()!.ToString();
            return $"       ↳ {m.DeclaringType?.FullName!.Split('`')[0]}.{m.Name} ({frequency})";
        }));
        logger.LogInformation($"   🚀 Scheduled methods:\n{methodsDisplay}");
    }

    /// <summary>Scans <paramref name="serviceProvider"/>'s services for <c>[Cycle]</c> methods and schedules
    /// them; returns the methods registered so far.</summary>
    /// <exception cref="InvalidOperationException">A <c>[Cycle]</c> method has parameters or another return type.</exception>
    public List<MethodInfo> RegisterMethods(IServiceProvider serviceProvider)
    {
        // 1. Collect all methods annotated with CycleAttribute
        foreach (var service in serviceProvider.GetAll<object>())
        {
            var type = service.GetType();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                              .Where(m => m.GetCustomAttribute<CycleAttribute>() != null);

            foreach (var method in methods)
            {
                RegisterMethod(method, service);
            }
        }

        // 2. Register scheduling logic (cron/frequency/realtime)
        foreach (var methodEnty in _registeredMethodsByType)
        {
            var methods = methodEnty.Value.Item2;
            var serviceInstance = methodEnty.Value.Item1;

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<CycleAttribute>();
                if (attr == null)
                    continue;

                if (attr.IsCron())
                {
                    RegisterCronJob(method, attr.Cron!, serviceInstance);
                }
                else if (attr.IsConfigured())
                {
                    RegisterFrequencyJob(method, ResolveConfiguredRate(attr), serviceInstance);
                }
                else if (attr.IsFrequency())
                {
                    RegisterFrequencyJob(method, attr.Rate, serviceInstance);
                }
                else if (attr.IsRealTime())
                {
                    // Every engine frame.
                    RegisterFrequencyJob(method, null, serviceInstance);
                }
            }
        }

        return _registeredMethodsByType.Values.SelectMany(x => x.Item2).ToList();
    }


    /// <summary>
    /// <c>[Cycle(Config = "key", Default = n, Unit = ...)]</c>: the rate is read from configuration
    /// at registration; a missing, empty or non-positive value falls back to <c>Default</c>, and
    /// without a usable default the method runs every frame.
    /// </summary>
    private CycleRate? ResolveConfiguredRate(CycleAttribute attr)
    {
        var cfg = _serviceProvider.GetService<IConfiguration>();
        var raw = cfg?[attr.Config!];
        var value = int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : attr.Default;
        return value > 0 ? new CycleRate(value, attr.Unit) : null;
    }

    private void RegisterMethod(MethodInfo method, object? serviceInstance)
    {
        var declaringType = method.DeclaringType;
        if (declaringType == null)
        {
            throw new InvalidOperationException("Method does not have a DeclaringType.");
        }

        var instanceDeclaringType = serviceInstance?.GetType() ?? declaringType;

        if (_registeredMethodsByType.TryGetValue(instanceDeclaringType, out var existingMethod))
        {
            var existingMethodInfo = existingMethod.Item2.FirstOrDefault(m => m.ToString() == method.ToString());

            if (existingMethodInfo != null)
            {
                if (instanceDeclaringType.IsSubclassOf(existingMethodInfo.DeclaringType!))
                {
                    existingMethod.Item2.Remove(existingMethodInfo);
                    existingMethod.Item2.Add(method);
                }
            }
            else
            {
                existingMethod.Item2.Add(method);
            }
        }
        else
        {
            bool methodReplaced = false;
            foreach (var entry in _registeredMethodsByType)
            {
                if (instanceDeclaringType.IsSubclassOf(entry.Key))
                {
                    var existingMethodInfo = entry.Value.Item2.FirstOrDefault(m => m.ToString() == method.ToString());
                    if (existingMethodInfo != null)
                    {
                        entry.Value.Item2.Remove(existingMethodInfo);
                        methodReplaced = true;

                        if (entry.Value.Item2.Count == 0)
                        {
                            _registeredMethodsByType.Remove(entry.Key);
                        }

                        _registeredMethodsByType[instanceDeclaringType] = (serviceInstance, new HashSet<MethodInfo> { method });
                        break;
                    }
                }
            }

            if (!methodReplaced)
            {
                _registeredMethodsByType[instanceDeclaringType] = (serviceInstance, new HashSet<MethodInfo> { method });
            }
        }
    }

    /// <summary>Registers <paramref name="method"/> on <paramref name="serviceInstance"/> as a cron job
    /// (standard 5-field Cronos expression, evaluated in UTC). Cron jobs run on the thread pool, not on
    /// the engine thread.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="serviceInstance"/> is null or the method signature is unsupported.</exception>
    public void RegisterCronJob(MethodInfo method, string cronExpression, object? serviceInstance = null)
    {

        var jobDelegate = CreateTaskDelegate(method, serviceInstance);
        _engine.RegisterCronJob(jobDelegate, cronExpression, serviceInstance);
    }

    private void RegisterFrequencyJob(MethodInfo method, CycleRate? cycleRate = null, object? serviceInstance = null)
    {
        var taskDelegate = CreateTaskDelegate(method, serviceInstance);
        _engine.ScheduleTask(taskDelegate, cycleRate);
    }

    private Func<Task> CreateTaskDelegate(MethodInfo method, object? serviceInstance = null)
    {
        if (serviceInstance == null)
            throw new InvalidOperationException($"Service instance for {method.DeclaringType!.FullName} could not be resolved.");

        // A static method has no target: binding it to the service instance would fail.
        var target = method.IsStatic ? null : serviceInstance;

        if (method.ReturnType == typeof(Task) && method.GetParameters().Length == 0)
        {
            var del = (Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), target, method);
            return del;
        }

        if (method.ReturnType == typeof(void) && method.GetParameters().Length == 0)
        {
            var del = (Action)Delegate.CreateDelegate(typeof(Action), target, method);
            return () =>
            {
                del();
                return Task.CompletedTask;
            };
        }

        throw new InvalidOperationException("Only parameterless methods returning Task or void are supported.");
    }
}


/// <summary>A task registered once with <see cref="IEngineCore.ScheduleTask"/> (a <c>[Cycle]</c> method):
/// the engine's internal bookkeeping record. Not normally created by game code.</summary>
public class EngineStaticTask
{
    private static long _nextSeq;

    /// <summary>Unique id (delegate method name plus a process-wide sequence number).</summary>
    public TaskIdentifier Id { get; }
    /// <summary>The delegate the engine invokes.</summary>
    public Delegate Delegate { get; }
    /// <summary>How often it runs.</summary>
    public CycleRate CycleRate { get; }
    /// <summary>Stopwatch timestamp given at registration; the engine schedules from its own last-run bookkeeping.</summary>
    public long NextExecuteTime { get; set; }

    /// <summary>Creates the record with a fresh unique <see cref="Id"/>.</summary>
    public EngineStaticTask(Delegate task, CycleRate cycleRate, long nextExecuteTime)
    {
        Delegate = task;
        CycleRate = cycleRate;
        NextExecuteTime = nextExecuteTime;
        // Each scheduled task needs a unique Id for the scheduler's per-task
        // last-run and in-flight bookkeeping. TaskIdentifier.FromDelegate keys
        // by method+declaringType — for `void` [Cycle] handlers that share a
        // compiler-generated wrapper lambda, those keys collide. Without the
        // sequence suffix, the second handler onward would inherit the first's
        // last-run timestamp and be permanently treated as "not due".
        var seq = System.Threading.Interlocked.Increment(ref _nextSeq);
        Id = new TaskIdentifier($"{TaskIdentifier.FromDelegate(task).Id}#{seq}");
    }
}


/// <summary>
/// The <see cref="IAltruistEngine"/> registered when <c>altruist:game:engine:diagnostics</c> is <c>false</c>:
/// a pass-through to the core engine (<see cref="AltruistEngine"/>). Inject <see cref="IAltruistEngine"/>
/// in game code rather than this type.
/// </summary>
[Service(typeof(IAltruistEngine))]
[ConditionalOnConfig("altruist:game:engine:diagnostics", havingValue: "false")]
public class EngineWithoutDiagnostics : IAltruistEngine
{
    private readonly IEngineCore _core;

    /// <summary>Wraps <paramref name="core"/>.</summary>
    public EngineWithoutDiagnostics(IEngineCore core)
    {
        _core = core;
    }

    /// <inheritdoc/>
    public CycleRate Rate => _core.Rate;

    /// <inheritdoc/>
    public bool Enabled => _core.Enabled;

    /// <inheritdoc/>
    public void Enable() => _core.Enable();

    /// <inheritdoc/>
    public void Disable() => _core.Disable();

    /// <inheritdoc/>
    public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
        => _core.RegisterCronJob(jobDelegate, cronExpression, serviceInstance);

    /// <inheritdoc/>
    public void Start(CancellationToken token) => _core.Start(token);

    /// <inheritdoc/>
    public void Stop() => _core.Stop();

    /// <inheritdoc/>
    public void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null)
        => _core.ScheduleTask(taskDelegate, cycleRate);

    /// <inheritdoc/>
    public void SendTask(TaskIdentifier taskId, Delegate taskDelegate)
        => _core.SendTask(taskId, taskDelegate);
    /// <inheritdoc/>
    public TaskIdentifier ScheduleEffect(CycleRate cycleRate, DateTime expiresAtUtc, Action<float> step)
    {
        return _core.ScheduleEffect(cycleRate, expiresAtUtc, step);
    }
    /// <inheritdoc/>
    public bool CancelEffect(TaskIdentifier id)
    {
        return _core.CancelEffect(id);
    }

    /// <inheritdoc/>
    public void WaitForNextTick(Delegate task)
    {
        _core.WaitForNextTick(task);
    }

    /// <inheritdoc/>
    public void WaitForNextTick(Action task)
    {
        _core.WaitForNextTick(task);
    }

    /// <inheritdoc/>
    public void WaitForNextTick(Func<Task> task)
    {
        _core.WaitForNextTick(task);
    }

    /// <inheritdoc/>
    public void SyncCommit(Action commit) => _core.SyncCommit(commit);
    /// <inheritdoc/>
    public Task<T> SyncCommit<T>(Func<T> commit) => _core.SyncCommit(commit);

    /// <inheritdoc/>
    public long Frame => _core.Frame;
    /// <inheritdoc/>
    public TaskIdentifier ScheduleOnce(TimeSpan delay, Action action) => _core.ScheduleOnce(delay, action);
    /// <inheritdoc/>
    public TaskIdentifier ScheduleAtFrame(long frame, Action action) => _core.ScheduleAtFrame(frame, action);
    /// <inheritdoc/>
    public void RunOffTick<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> onTick) => _core.RunOffTick(work, onTick);
    /// <inheritdoc/>
    public void RunOffTick(Func<CancellationToken, Task> work, Action<Exception?> onTick) => _core.RunOffTick(work, onTick);

    /// <summary>
    /// The <see cref="IAltruistEngine"/> registered when <c>altruist:game:engine:diagnostics</c> is <c>true</c>:
    /// forwards to the core engine and times every scheduled task, logging an estimated throughput
    /// every N tasks. For profiling only; inject <see cref="IAltruistEngine"/> rather than this type.
    /// </summary>
    [Service(typeof(IAltruistEngine))]
    [ConditionalOnConfig("altruist:game:engine:diagnostics", havingValue: "true")]
    public class EngineWithDiagnostics : IAltruistEngine
    {
        private readonly IEngineCore _wrappedEngine;
        private readonly ILogger _logger;

        private readonly double _engineFrequencyHz;

        private readonly int _taskTrackCount = 100;


        /// <summary>Wraps <paramref name="wrappedEngine"/>.</summary>
        public EngineWithDiagnostics(IEngineCore wrappedEngine, ILoggerFactory loggerFactory)
        {
            _wrappedEngine = wrappedEngine;
            _logger = loggerFactory.CreateLogger<EngineWithDiagnostics>();
            var unit = _wrappedEngine.Rate.Unit;

            if (unit == CycleUnit.Seconds)
            {
                // Value = ticks per cycle => Hz = ticks per second / ticks per cycle
                _engineFrequencyHz = (double)TimeSpan.TicksPerSecond / _wrappedEngine.Rate.Value;
            }
            else if (unit == CycleUnit.Milliseconds)
            {
                // Value = ticks per cycle => Hz = ticks per millisecond / ticks per cycle
                _taskTrackCount = 1_000;
                _engineFrequencyHz = (double)(TimeSpan.TicksPerSecond / 1000) / _wrappedEngine.Rate.Value;
            }
            else
            {
                // Value = frequency in Hz directly (per TICK-based scheduling, i.e., "X times per tick")
                // In this case, the higher the number, the **slower** it is.
                // So to get Hz as "X times per second", we need Stopwatch.Frequency / Value
                _taskTrackCount = 1_000_000;
                _engineFrequencyHz = (double)Stopwatch.Frequency / _wrappedEngine.Rate.Value;
            }
        }

        /// <inheritdoc/>
        public CycleRate Rate => _wrappedEngine.Rate;

        /// <inheritdoc/>
        public bool Enabled { get; private set; }

        /// <inheritdoc/>
        public void Enable()
        {
            Enabled = true;
        }

        /// <inheritdoc/>
        public void Disable()
        {
            Enabled = false;
        }

        /// <inheritdoc/>
        public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
        {
            _wrappedEngine.RegisterCronJob(jobDelegate, cronExpression, serviceInstance);
        }

        /// <inheritdoc/>
        public void Start(CancellationToken token)
        {
            _wrappedEngine.Start(token);
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _wrappedEngine.Stop();
        }


        private long _accumulatedTicks = 0;
        private int _taskCount;

        private async Task ExecuteWithDiagnostics(Func<Task> task)
        {
            var stopwatch = Stopwatch.StartNew();
            await task();
            stopwatch.Stop();
            RecordDiagnostics(stopwatch.ElapsedTicks);
        }

        private void ExecuteWithDiagnostics(Action task)
        {
            var stopwatch = Stopwatch.StartNew();
            task();
            stopwatch.Stop();
            RecordDiagnostics(stopwatch.ElapsedTicks);
        }

        private void RecordDiagnostics(long elapsedTicks)
        {
            _accumulatedTicks += elapsedTicks;
            _taskCount++;

            if (_taskCount >= _taskTrackCount)
            {
                double elapsedTimeInNanoseconds = _accumulatedTicks * 1_000_000_000.0 / Stopwatch.Frequency;
                double elapsedTimePerTask = elapsedTimeInNanoseconds / _taskCount;
                double elapsedTimePerTaskInSeconds = elapsedTimePerTask / 1_000_000_000.0;
                double tasksPerSecond = 1 / elapsedTimePerTaskInSeconds;

                _logger.LogInformation(
                    $"⚡ Uh, ah I am fast ⎚-⎚ uh ah! " +
                    $"Just processed {_taskTrackCount} tasks in {elapsedTimeInNanoseconds:n0}ns. " +
                    $"Match that! (⎚-⎚)\n\n" +

                    $"📊 Theoretical Throughput:\n" +
                    $"   - Estimated max capacity: {tasksPerSecond:n0} tasks/sec\n" +
                    $"   - Configured frequency: {_engineFrequencyHz:n2} Hz\n\n" +

                    $"🚀 Engine Efficiency:\n" +
                    $"   - Running at {tasksPerSecond / _engineFrequencyHz * 100:n2}% of its configured frequency.\n" +
                    $"   - {tasksPerSecond / _engineFrequencyHz:n2}x faster than expected.\n"
                );

                // Reset counters
                _taskCount = 0;
                _accumulatedTicks = 0;
            }
        }

        /// <inheritdoc/>
        public void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null)
        {
            // null = every frame (resolved by the wrapped engine).
            var actualHz = cycleRate;

            if (taskDelegate is Func<Task> asyncDelegate)
            {
                var wrappedDelegate = async () =>
                {
                    await ExecuteWithDiagnostics(asyncDelegate);
                };
                _wrappedEngine.ScheduleTask(wrappedDelegate, actualHz);
            }
            else if (taskDelegate is Action syncDelegate)
            {
                var wrappedDelegate = () =>
                {
                    ExecuteWithDiagnostics(syncDelegate);
                };
                _wrappedEngine.ScheduleTask(wrappedDelegate, actualHz);
            }
        }

        /// <inheritdoc/>
        public void SendTask(TaskIdentifier taskId, Delegate taskDelegate)
        {
            if (taskDelegate is Func<Task> asyncDelegate)
            {
                var wrappedDelegate = async () =>
                {
                    await ExecuteWithDiagnostics(asyncDelegate);
                };
                _wrappedEngine.SendTask(taskId, wrappedDelegate);
            }
            else if (taskDelegate is Action syncDelegate)
            {
                var wrappedDelegate = () =>
                {
                    ExecuteWithDiagnostics(syncDelegate);
                };
                _wrappedEngine.SendTask(taskId, wrappedDelegate);
            }
        }

        /// <inheritdoc/>
        public TaskIdentifier ScheduleEffect(CycleRate cycleRate, DateTime expiresAtUtc, Action<float> step)
        {
            return _wrappedEngine.ScheduleEffect(cycleRate, expiresAtUtc, step);
        }
        /// <inheritdoc/>
        public bool CancelEffect(TaskIdentifier id)
        {
            return _wrappedEngine.CancelEffect(id);
        }

        /// <inheritdoc/>
        public void WaitForNextTick(Delegate task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }

        /// <inheritdoc/>
        public void WaitForNextTick(Action task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }
        /// <inheritdoc/>
        public void WaitForNextTick(Func<Task> task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }

        /// <inheritdoc/>
        public void SyncCommit(Action commit)
        {
            _wrappedEngine.SyncCommit(commit);
        }
        /// <inheritdoc/>
        public Task<T> SyncCommit<T>(Func<T> commit)
        {
            return _wrappedEngine.SyncCommit(commit);
        }

        /// <inheritdoc/>
        public long Frame => _wrappedEngine.Frame;
        /// <inheritdoc/>
        public TaskIdentifier ScheduleOnce(TimeSpan delay, Action action) => _wrappedEngine.ScheduleOnce(delay, action);
        /// <inheritdoc/>
        public TaskIdentifier ScheduleAtFrame(long frame, Action action) => _wrappedEngine.ScheduleAtFrame(frame, action);
        /// <inheritdoc/>
        public void RunOffTick<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> onTick) => _wrappedEngine.RunOffTick(work, onTick);
        /// <inheritdoc/>
        public void RunOffTick(Func<CancellationToken, Task> work, Action<Exception?> onTick) => _wrappedEngine.RunOffTick(work, onTick);
    }

}
