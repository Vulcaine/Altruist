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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Altruist.Engine;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Engine;

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


public class MethodScheduler
{
    private readonly IAltruistEngine _engine;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, (object? serviceInstance, HashSet<MethodInfo>)> _registeredMethodsByType;

    public MethodScheduler(IAltruistEngine engine, IServiceProvider serviceProvider)
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
                if (attr == null) continue;

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
        // var serviceInstance = GetServiceInstance(method.DeclaringType!).Item1;
        var parameters = method.GetParameters().Length == 0 ? null : new object[0];

        if (serviceInstance == null)
        {
            throw new InvalidOperationException($"Service instance for {method.DeclaringType!.FullName} could not be resolved.");
        }

        if (method.ReturnType == typeof(Task))
        {
            return () => (Task)method.Invoke(serviceInstance, parameters)!;
        }
        else if (method.ReturnType == typeof(void))
        {
            return () =>
            {
                method.Invoke(serviceInstance, parameters);
                return Task.CompletedTask;
            };
        }

        throw new InvalidOperationException("Only methods returning Task or void are supported.");
    }

    private (object? serviceInstance, Type resolvedType) GetServiceInstance(Type type)
    {
        var serviceInstance = _serviceProvider.GetService(type);

        Type? resolvedType = null;
        if (serviceInstance == null)
        {
            serviceInstance = TryResolveFromInterfaces(type, out resolvedType);
        }

        return (serviceInstance, resolvedType ?? type);
    }

    private object? TryResolveFromInterfaces(Type type, out Type? resolvedType)
    {
        resolvedType = null;

        foreach (var interfaceType in type.GetInterfaces())
        {
            var serviceInstance = _serviceProvider.GetService(interfaceType);
            if (serviceInstance != null)
            {
                resolvedType = interfaceType;
                return serviceInstance;
            }
        }

        return null;
    }
}


public class EngineStaticTask
{
    public Delegate Delegate { get; }
    public CycleRate CycleRate { get; }
    public long NextExecuteTime { get; set; }

    public EngineStaticTask(Delegate task, CycleRate cycleRate, long nextExecuteTime) => (Delegate, CycleRate, NextExecuteTime) = (task, cycleRate, nextExecuteTime);
}

public class AltruistEngine : IAltruistEngine
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LinkedList<EngineStaticTask> _staticTasks; // that are scheduled to the engine
    private readonly ConcurrentDictionary<TaskIdentifier, Delegate> _dynamicTasks; // that are sent to the engine dynamically
    private readonly Dictionary<string, Action> _precompiledDelegatesCache = new(); // precompiled delegates with dependencies

    private CancellationTokenSource _cancellationTokenSource;
    private readonly CycleRate _engineRate;
    private readonly int _preallocatedTaskSize;
    private Thread _engineThread;

    public CycleRate Rate => _engineRate;

    public bool Enabled { get; private set; }

    public int Throttle = 1000000;

    private IServerStatus _appStatus;

    private readonly GameWorldCoordinator _worldCoordinator;

    public AltruistEngine(
        IServiceProvider serviceProvider,
        GameWorldCoordinator worldCoordinator,
        int engineFrequencyHz = 30, CycleUnit unit = CycleUnit.Ticks, int? throttle = null)
    {
        var settings = serviceProvider.GetRequiredService<IAltruistContext>();
        _staticTasks = new LinkedList<EngineStaticTask>();
        _dynamicTasks = new ConcurrentDictionary<TaskIdentifier, Delegate>();
        _engineRate = new CycleRate(engineFrequencyHz, unit);
        Throttle = throttle ?? (int)(1_000_000_000 / (_engineRate.Value + 1));
        _preallocatedTaskSize = Throttle;
        _cancellationTokenSource = new CancellationTokenSource();
        _engineThread = null!;
        _serviceProvider = serviceProvider;
        _appStatus = settings.AppStatus;
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

    public void SendTask(TaskIdentifier taskId, Delegate taskDelegate)
    {
        _dynamicTasks.AddOrUpdate(taskId, taskDelegate, (_, _) => taskDelegate);
    }

    public void Start()
    {
        if (!Enabled)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _engineThread = new Thread(() =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    if (_appStatus.Status == ReadyState.Alive)
                    {
                        Enable();
                        RunEngineLoop();
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
        }
    }

    private async Task RunEngineLoop()
    {
        long _engineFrequencyTicks = _engineRate.Value;
        var dynamicTasks = new List<Task>(_preallocatedTaskSize);
        var stopwatch = Stopwatch.StartNew();

        long lastTick = stopwatch.ElapsedTicks;
        var asyncTaskList = new List<Task>();

        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            long currentTick = stopwatch.ElapsedTicks;
            long elapsedTicks = currentTick - lastTick;
            double deltaTime = (double)elapsedTicks / Stopwatch.Frequency;

            if (elapsedTicks >= _engineFrequencyTicks)
            {
                lastTick = currentTick;

                foreach (var task in _staticTasks)
                {
                    if (elapsedTicks >= task.CycleRate.Value)
                    {
                        asyncTaskList.Add(ExecuteTaskAsync(task.Delegate));
                    }
                }

                if (asyncTaskList.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(asyncTaskList);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to execute tasks: {ex}");
                    }

                    asyncTaskList.Clear();
                }

                foreach (var task in _dynamicTasks.Values)
                {
                    dynamicTasks.Add(ExecuteTaskAsync(task));
                }

                if (dynamicTasks.Count >= Throttle)
                {
                    await Task.WhenAll(dynamicTasks);
                }
                else
                {
                    dynamicTasks.Clear();
                }

                _dynamicTasks.Clear();
                _worldCoordinator.Step((float)deltaTime);
            }
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
}


public class EngineWithDiagnostics : IAltruistEngine
{
    private readonly IAltruistEngine _wrappedEngine;
    private readonly ILogger _logger;

    private int _taskCount;
    private long _accumulatedMillis = 0;

    public EngineWithDiagnostics(IAltruistEngine wrappedEngine, ILoggerFactory loggerFactory)
    {
        _wrappedEngine = wrappedEngine;
        _taskCount = 0;
        _accumulatedMillis = 0;
        _logger = loggerFactory.CreateLogger<EngineWithDiagnostics>();
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

    public void Start()
    {
        _wrappedEngine.Start();
    }

    public void Stop()
    {
        _wrappedEngine.Stop();
    }


    private async Task ExecuteWithDiagnostics(Func<Task> task)
    {
        var stopwatch = Stopwatch.StartNew();

        await task();

        stopwatch.Stop();

        // Accumulate elapsed time in ticks (higher precision than milliseconds)
        _accumulatedMillis += stopwatch.ElapsedTicks;
        _taskCount++;
        if (_taskCount >= 1_000_000)
        {
            double elapsedTimeInNanoseconds = _accumulatedMillis * 1_000_000_000.0 / Stopwatch.Frequency;

            double elapsedTimePerTask = elapsedTimeInNanoseconds / _taskCount;
            double elapsedTimePerTaskInSeconds = elapsedTimePerTask / 1_000_000_000.0;

            double tasksPerSecond = 1 / elapsedTimePerTaskInSeconds;
            double engineFrequencyHz = _wrappedEngine.Rate.Value;

            _logger.LogInformation(
                $"⚡ Uh, ah I am fast ⎚-⎚ uh ah! " +
                $"Just processed 1,000,000 tasks in {elapsedTimeInNanoseconds:n0}ns. " +
                $"Match that! (⎚-⎚)\n\n" +

                $"📊 Theoretical Throughput:\n" +
                $"   - Estimated max capacity: {tasksPerSecond:n0} tasks/sec\n" +
                $"   - Configured frequency: {engineFrequencyHz} Hz\n\n" +

                $"🚀 Engine Efficiency:\n" +
                $"   - Running at {(tasksPerSecond / engineFrequencyHz) * 100:n2}% of its configured frequency.\n" +
                $"   - {tasksPerSecond / engineFrequencyHz:n2}x faster than expected.\n"
            );


            // Reset for the next set of measurements
            _taskCount = 0;
            _accumulatedMillis = 0;
        }
    }


    private void ExecuteWithDiagnostics(Action task)
    {
        var stopwatch = Stopwatch.StartNew();

        task();

        stopwatch.Stop();

        _accumulatedMillis += stopwatch.ElapsedMilliseconds;
        _taskCount++;

        if (_taskCount >= 1_000_000)
        {
            double elapsedTimeInNanoseconds = _accumulatedMillis * 1_000_000;

            double elapsedTimePerTask = elapsedTimeInNanoseconds / _taskCount;
            double elapsedTimePerTaskInSeconds = elapsedTimePerTask / 1_000_000_000;

            double tasksPerSecond = 1 / elapsedTimePerTaskInSeconds;
            double engineFrequencyHz = _wrappedEngine.Rate.Value;

            _logger.LogInformation(
                $"⚡ Uh, ah I am fast ⎚-⎚ uh ah! " +
                $"Jut processed 1,000,000 tasks in {elapsedTimeInNanoseconds:n0}ns. " +
                $"Match that! (⎚-⎚)\n\n" +

                $"📊 Theoretical Throughput:\n" +
                $"   - Estimated max capacity: {tasksPerSecond:n0} tasks/sec\n" +
                $"   - Configured frequency: {engineFrequencyHz} Hz\n\n" +

                $"🚀 Engine Efficiency:\n" +
                $"   - Running at {(tasksPerSecond / engineFrequencyHz) * 100:n2}% of its configured frequency.\n" +
                $"   - {tasksPerSecond / engineFrequencyHz:n2}x faster than expected.\n"
            );

            _taskCount = 0;
            _accumulatedMillis = 0;
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
}

