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

public static class FrameTime
{
    private static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    public static long NowTicks => Stopwatch.GetTimestamp();
    public static float TicksToDeltaSeconds(long ticks) => (float)(ticks * TicksToSeconds);
}

[Service]
public class MethodScheduler
{
    private readonly IEngineCore _engine;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, (object? serviceInstance, HashSet<MethodInfo>)> _registeredMethodsByType;

    public MethodScheduler(IEngineCore engine, IServiceProvider serviceProvider)
    {
        _engine = engine;
        _serviceProvider = serviceProvider;
        _registeredMethodsByType = new Dictionary<Type, (object? serviceInstance, HashSet<MethodInfo>)>();
    }

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
                else if (attr.IsFrequency())
                {
                    RegisterFrequencyJob(method, attr.Rate, serviceInstance);
                }
                else if (attr.IsRealTime())
                {
                    var engine = _serviceProvider.GetService<IAltruistEngine>();
                    RegisterFrequencyJob(method, engine!.Rate, serviceInstance);
                }
            }
        }

        return _registeredMethodsByType.Values.SelectMany(x => x.Item2).ToList();
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

        if (method.ReturnType == typeof(Task) && method.GetParameters().Length == 0)
        {
            var del = (Func<Task>)Delegate.CreateDelegate(typeof(Func<Task>), serviceInstance, method);
            return del;
        }

        if (method.ReturnType == typeof(void) && method.GetParameters().Length == 0)
        {
            var del = (Action)Delegate.CreateDelegate(typeof(Action), serviceInstance, method);
            return () =>
            {
                del();
                return Task.CompletedTask;
            };
        }

        throw new InvalidOperationException("Only parameterless methods returning Task or void are supported.");
    }
}


public class EngineStaticTask
{
    public TaskIdentifier Id { get; }
    public Delegate Delegate { get; }
    public CycleRate CycleRate { get; }
    public long NextExecuteTime { get; set; }

    public EngineStaticTask(Delegate task, CycleRate cycleRate, long nextExecuteTime)
    {
        Delegate = task;
        CycleRate = cycleRate;
        NextExecuteTime = nextExecuteTime;
        Id = TaskIdentifier.FromDelegate(task);
    }
}


[Service(typeof(IAltruistEngine))]
[ConditionalOnConfig("altruist:game:engine:diagnostics", havingValue: "false")]
public class EngineWithoutDiagnostics : IAltruistEngine
{
    private readonly IEngineCore _core;

    public EngineWithoutDiagnostics(IEngineCore core)
    {
        _core = core;
    }

    public CycleRate Rate => _core.Rate;

    public bool Enabled => _core.Enabled;

    public void Enable() => _core.Enable();

    public void Disable() => _core.Disable();

    public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
        => _core.RegisterCronJob(jobDelegate, cronExpression, serviceInstance);

    public void Start(CancellationToken token) => _core.Start(token);

    public void Stop() => _core.Stop();

    public void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null)
        => _core.ScheduleTask(taskDelegate, cycleRate);

    public void SendTask(TaskIdentifier taskId, Delegate taskDelegate)
        => _core.SendTask(taskId, taskDelegate);
    public TaskIdentifier ScheduleEffect(CycleRate cycleRate, DateTime expiresAtUtc, Action<float> step)
    {
        return _core.ScheduleEffect(cycleRate, expiresAtUtc, step);
    }
    public bool CancelEffect(TaskIdentifier id)
    {
        return _core.CancelEffect(id);
    }

    public void WaitForNextTick(Delegate task)
    {
        _core.WaitForNextTick(task);
    }

    public void WaitForNextTick(Action task)
    {
        _core.WaitForNextTick(task);
    }

    public void WaitForNextTick(Func<Task> task)
    {
        _core.WaitForNextTick(task);
    }

    public void SyncCommit(Action commit) => throw new NotImplementedException();
    public Task<T> SyncCommit<T>(Func<T> commit) => throw new NotImplementedException();

    [Service(typeof(IAltruistEngine))]
    [ConditionalOnConfig("altruist:game:engine:diagnostics", havingValue: "true")]
    public class EngineWithDiagnostics : IAltruistEngine
    {
        private readonly IEngineCore _wrappedEngine;
        private readonly ILogger _logger;

        private readonly double _engineFrequencyHz;

        private readonly int _taskTrackCount = 100;


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

        public CycleRate Rate => _wrappedEngine.Rate;

        public bool Enabled { get; private set; }

        public void Enable()
        {
            Enabled = true;
        }

        public void Disable()
        {
            Enabled = false;
        }

        public void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null)
        {
            _wrappedEngine.RegisterCronJob(jobDelegate, cronExpression, serviceInstance);
        }

        public void Start(CancellationToken token)
        {
            _wrappedEngine.Start(token);
        }

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

        public void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null)
        {
            var actualHz = cycleRate ?? _wrappedEngine.Rate;

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

        public TaskIdentifier ScheduleEffect(CycleRate cycleRate, DateTime expiresAtUtc, Action<float> step)
        {
            return _wrappedEngine.ScheduleEffect(cycleRate, expiresAtUtc, step);
        }
        public bool CancelEffect(TaskIdentifier id)
        {
            return _wrappedEngine.CancelEffect(id);
        }

        public void WaitForNextTick(Delegate task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }

        public void WaitForNextTick(Action task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }
        public void WaitForNextTick(Func<Task> task)
        {
            _wrappedEngine.WaitForNextTick(task);
        }

        public void SyncCommit(Action commit)
        {
            _wrappedEngine.SyncCommit(commit);
        }
        public Task<T> SyncCommit<T>(Func<T> commit)
        {
            return _wrappedEngine.SyncCommit(commit);
        }
    }

}
