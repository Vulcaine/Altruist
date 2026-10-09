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

namespace Altruist.Engine;


/// <summary>
/// Names an engine task or effect. Equality is by <see cref="Id"/> (ordinal), so two identifiers with the same id
/// refer to the same task: <see cref="IEngineCore.SendTask"/> coalesces requests per id, and effect/timer handles
/// returned by the scheduling methods are compared this way.
/// </summary>
/// <remarks>
/// Pick a stable id (e.g. <c>"respawn:" + playerId</c>) when repeated requests should share one queue and never run
/// concurrently; pick a unique id when every request must run independently.
/// </remarks>
public class TaskIdentifier : IEquatable<TaskIdentifier>
{
    /// <summary>The identifier string (compared ordinally).</summary>
    public string Id { get; }

    /// <summary>A random value drawn at construction (not part of equality); a jitter seed, not for security.</summary>
    public int Seed { get; }

    /// <summary>Creates an identifier.</summary>
    /// <param name="id">The identifier string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public TaskIdentifier(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        // Not used for security — just a non-deterministic seed for task scheduling jitter
#pragma warning disable CA5394
        Seed = Random.Shared.Next();
#pragma warning restore CA5394
    }

    /// <summary>Ordinal comparison of <see cref="Id"/>.</summary>
    /// <param name="other">The identifier to compare with.</param>
    public bool Equals(TaskIdentifier? other)
    {
        if (other == null)
            return false;
        return Id.Equals(other.Id, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode();

    /// <summary>Creates an identifier from a type's full name (one task per type).</summary>
    /// <param name="type">The type.</param>
    public static TaskIdentifier FromType(Type type) => new(type.FullName!);

    /// <summary>
    /// Creates an identifier <c>DeclaringType.MethodName</c> from a delegate's target method. Note that lambdas declared in
    /// the same method can share a compiler-generated method name; use an explicit id when that matters.
    /// </summary>
    /// <param name="del">The delegate.</param>
    public static TaskIdentifier FromDelegate(Delegate del)
    {
        var method = del.Method;
        var declaringType = method.DeclaringType?.FullName ?? $"UnknownType_{Guid.NewGuid()}";
        var methodName = method.Name;
        return new TaskIdentifier($"{declaringType}.{methodName}");
    }

    /// <summary>Returns <see cref="Id"/>.</summary>
    public override string ToString() => Id;
}


/// <summary>
/// The game engine's fixed-rate frame loop and its schedulers. Registered as a singleton (<c>AltruistEngine</c>)
/// when <c>altruist:game:engine</c> is configured; the loop runs at <c>altruist:game:engine:framerateHz</c>
/// (alias <c>frequency</c>, default 30 Hz) on a dedicated engine thread once the server reports alive.
/// </summary>
/// <remarks>
/// <para>Each frame runs, in order: the next-tick queue (<see cref="WaitForNextTick(Action)"/>,
/// <see cref="SyncCommit(Action)"/>), the recurring tasks (<c>[Cycle]</c> methods and <see cref="ScheduleTask"/>), the
/// requested tasks (<see cref="SendTask"/>), the effects and timers (<see cref="ScheduleEffect"/>,
/// <see cref="ScheduleOnce"/>, <see cref="ScheduleAtFrame"/>), then the world step. An exception in any task is
/// logged (rate-limited) and never stops the loop.</para>
/// <para>Which API to use:
/// <list type="bullet">
/// <item>Mutate game state from a request/network/thread-pool thread: <see cref="WaitForNextTick(Action)"/> or <see cref="SyncCommit{T}"/> (to get a result back).</item>
/// <item>Fixed recurring work on a service: <c>[Cycle]</c> (declarative) or <see cref="ScheduleTask"/> (programmatic, registered once).</item>
/// <item>Work keyed by id that must not overlap itself and must run once per request: <see cref="SendTask"/>.</item>
/// <item>A repeating step with an expiry (buffs, damage over time): <see cref="ScheduleEffect"/>.</item>
/// <item>A one-shot delay timer: <see cref="ScheduleOnce"/> (engine clock) or <see cref="ScheduleAtFrame"/> (exact frame).</item>
/// <item>Slow I/O whose result the game needs: <see cref="RunOffTick{T}"/>.</item>
/// <item>Wall-clock jobs (daily resets): <see cref="RegisterCronJob"/>.</item>
/// </list></para>
/// <para>Units: a <see cref="CycleRate"/> with <see cref="CycleUnit.Ticks"/> (the default) counts engine frames between
/// runs (higher = slower); use <see cref="CycleUnit.Hz"/> for runs per second. <see cref="Frame"/> counts frames.</para>
/// <para>Game code usually injects <see cref="IAltruistEngine"/> (the same API; registered when
/// <c>altruist:game:engine:diagnostics</c> is set to <c>true</c> or <c>false</c>); <see cref="IEngineCore"/> is always
/// available when the engine is configured.</para>
/// </remarks>
/// <example>
/// <code>
/// // from a network handler: apply the change on the engine thread
/// engine.WaitForNextTick(() =&gt; world.Apply(input));
///
/// // a 5 s effect stepping 10 times per second
/// var id = engine.ScheduleEffect(new CycleRate(10, CycleUnit.Hz), DateTime.UtcNow.AddSeconds(5), dt =&gt; Heal(target, 2));
/// engine.CancelEffect(id);
///
/// // a one-shot timer
/// engine.ScheduleOnce(TimeSpan.FromSeconds(3), () =&gt; Respawn(playerId));
/// </code>
/// </example>
public interface IEngineCore
{
    /// <summary>The engine's configured rate (<c>framerateHz</c>/<c>frequency</c> with <c>altruist:game:engine:unit</c>); informational.</summary>
    CycleRate Rate { get; }
    /// <summary>Whether frames are being run. <see cref="Start"/> enables, <see cref="Stop"/> and <see cref="Disable"/> disable.</summary>
    bool Enabled { get; }
    /// <summary>Resumes running frames on a started engine (the loop keeps ticking but skips frames while disabled).</summary>
    void Enable();
    /// <summary>Pauses frame execution without stopping the engine thread. Use <see cref="Stop"/> to shut it down.</summary>
    void Disable();
    /// <summary>
    /// Runs a parameterless delegate on a wall-clock cron schedule (5-field, evaluated in UTC). Cron jobs run on the
    /// thread pool, independent of the frame loop and NOT on the engine thread. A returned <see cref="Task"/> is awaited,
    /// so runs of one job never overlap (an occurrence missed while the previous run is busy is skipped); a throwing job
    /// is logged and runs again at its next occurrence. Use <see cref="WaitForNextTick(Action)"/> inside the job to touch
    /// game state. <c>[Cycle("cron")]</c> methods are registered through this. For a rate in frames or Hz use
    /// <see cref="ScheduleTask"/> instead.
    /// </summary>
    /// <param name="jobDelegate">The parameterless job.</param>
    /// <param name="cronExpression">A 5-field cron expression, e.g. <see cref="CronPresets.Daily"/>.</param>
    /// <param name="serviceInstance">Unused by the default engine.</param>
    void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null);
    /// <summary>
    /// Starts the engine thread (no-op when already enabled). Frames begin once the server status is alive. Called by
    /// the framework at startup; application code rarely needs it.
    /// </summary>
    /// <param name="token">Stops the engine when cancelled.</param>
    void Start(CancellationToken token);
    /// <summary>Disables the engine and cancels its loop.</summary>
    void Stop();

    /// <summary>
    /// Runs <paramref name="taskDelegate"/> on the engine loop at <paramref name="cycleRate"/>;
    /// <c>null</c> = every frame.
    /// </summary>
    /// <remarks>
    /// The programmatic form of <c>[Cycle]</c>: register once (typically at startup); there is no unregister, so for
    /// work that must stop use <see cref="ScheduleEffect"/>. A run is skipped while the previous async run is still in
    /// flight (never overlaps itself). Parameters of <paramref name="taskDelegate"/> are resolved from DI once at
    /// registration. The task is adopted at the start of the next frame.
    /// </remarks>
    /// <param name="taskDelegate">The task; parameterless, or with DI-resolvable parameters.</param>
    /// <param name="cycleRate">Frames between runs for <see cref="CycleUnit.Ticks"/>, runs per second for <see cref="CycleUnit.Hz"/>; <c>null</c> = every frame.</param>
    /// <exception cref="ArgumentException">A <see cref="CycleUnit.Hz"/> rate above the engine frame rate.</exception>
    /// <exception cref="InvalidOperationException">A parameter cannot be resolved from DI.</exception>
    void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null);
    /// <summary>
    /// Requests one run of <paramref name="taskDelegate"/> on the engine loop, keyed by <paramref name="taskId"/>. Requests
    /// for the same id are counted (none are dropped) and run one at a time: a run starts in a frame only when the previous
    /// run for that id has finished, and the latest delegate for the id is used. At most 128 runs start per frame;
    /// order across ids is unspecified. Async delegates are started, not awaited by the frame.
    /// </summary>
    /// <remarks>Use a stable id to serialize work per entity; use <see cref="WaitForNextTick(Action)"/> when you just need
    /// strict FIFO order on the next frame.</remarks>
    /// <param name="taskId">The coalescing key.</param>
    /// <param name="taskDelegate">A parameterless delegate (<see cref="Action"/>, <see cref="Func{TResult}"/> of <see cref="Task"/>, or any delegate invoked without arguments).</param>
    void SendTask(TaskIdentifier taskId, Delegate taskDelegate);
    /// <summary>Queues a parameterless delegate for the next frame's next-tick phase. See <see cref="WaitForNextTick(Action)"/>.</summary>
    /// <param name="task">A parameterless delegate; a returned <see cref="Task"/> is awaited before the next queued item.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    void WaitForNextTick(Delegate task);
    /// <summary>
    /// Queues <paramref name="task"/> to run at the start of the next frame, on the engine loop, in FIFO order with other
    /// queued items. The standard way to mutate game state from another thread (request handlers, network gates, thread pool).
    /// Thread-safe; returns immediately.
    /// </summary>
    /// <param name="task">The action.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    void WaitForNextTick(Action task);
    /// <summary>
    /// Queues an async delegate for the next frame's next-tick phase. It is awaited before the next queued item runs, so
    /// a slow await delays the whole frame; keep I/O out of it (use <see cref="RunOffTick{T}"/>).
    /// </summary>
    /// <param name="task">The async delegate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is null.</exception>
    void WaitForNextTick(Func<Task> task);
    /// <summary>Same as <see cref="WaitForNextTick(Action)"/>: applies <paramref name="commit"/> on the next frame.</summary>
    /// <param name="commit">The state change.</param>
    void SyncCommit(Action commit);
    /// <summary>
    /// Runs <paramref name="commit"/> on the next frame (next-tick phase) and returns its result. Await the task from
    /// outside the engine loop; awaiting it from inside the next-tick phase would deadlock the frame.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="commit">The function to run on the engine loop.</param>
    /// <returns>A task completing with the result, or faulting with the exception <paramref name="commit"/> threw.</returns>
    Task<T> SyncCommit<T>(Func<T> commit);
    /// <summary>
    /// Runs <paramref name="step"/> repeatedly on the engine loop (effect phase) at <paramref name="cycleRate"/> until
    /// <paramref name="expiresAtUtc"/>; returns a handle for <see cref="CancelEffect"/>. A step that throws removes the effect.
    /// </summary>
    /// <remarks>
    /// With <see cref="CycleUnit.Ticks"/> the first run is <c>Value</c> frames after scheduling; with time units it is one
    /// interval later. Time-based effects do not burst to catch up after a stall.
    /// </remarks>
    /// <param name="cycleRate">How often to step (frames between runs for <see cref="CycleUnit.Ticks"/>, runs per second for <see cref="CycleUnit.Hz"/>).</param>
    /// <param name="expiresAtUtc">UTC deadline after which the effect is removed.</param>
    /// <param name="step">The step; its argument is the current engine frame's delta time in seconds (not the time since the effect's previous step).</param>
    /// <returns>The effect's identifier.</returns>
    TaskIdentifier ScheduleEffect(
         CycleRate cycleRate,
         DateTime expiresAtUtc,
         Action<float> step);


    /// <summary>Cancels an effect or timer scheduled with <see cref="ScheduleEffect"/>, <see cref="ScheduleOnce"/> or <see cref="ScheduleAtFrame"/>.</summary>
    /// <param name="id">The handle returned when scheduling.</param>
    /// <returns><c>true</c> if it was still pending; <c>false</c> if it already ran, expired or was cancelled.</returns>
    bool CancelEffect(TaskIdentifier id);

    /// <summary>Engine frames run so far (the frame being run, while inside one).</summary>
    long Frame => throw new NotSupportedException($"{GetType().Name} does not count frames.");

    /// <summary>
    /// Runs <paramref name="action"/> once on the engine loop (in the effect phase of a frame) after
    /// <paramref name="delay"/>, measured on the engine clock. Cancel with <see cref="CancelEffect"/>.
    /// </summary>
    TaskIdentifier ScheduleOnce(TimeSpan delay, Action action) =>
        throw new NotSupportedException($"{GetType().Name} does not support ScheduleOnce.");

    /// <summary>
    /// Runs <paramref name="action"/> once on the engine loop (in the effect phase) of engine frame
    /// <paramref name="frame"/>, or of the next frame if that one has passed. Cancel with <see cref="CancelEffect"/>.
    /// </summary>
    TaskIdentifier ScheduleAtFrame(long frame, Action action) =>
        throw new NotSupportedException($"{GetType().Name} does not support ScheduleAtFrame.");

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool (never on the engine loop) and hands its result,
    /// or its exception, to <paramref name="onTick"/> on the engine loop (next-tick queue).
    /// For I/O whose outcome the game state needs: database writes, HTTP calls.
    /// </summary>
    void RunOffTick<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> onTick)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));
        if (onTick is null)
            throw new ArgumentNullException(nameof(onTick));
        _ = Task.Run(async () =>
        {
            T? result = default;
            Exception? error = null;
            try
            { result = await work(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
            WaitForNextTick(() => onTick(result, error));
        });
    }

    /// <summary><see cref="RunOffTick{T}"/> for work without a result.</summary>
    void RunOffTick(Func<CancellationToken, Task> work, Action<Exception?> onTick)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));
        if (onTick is null)
            throw new ArgumentNullException(nameof(onTick));
        RunOffTick<bool>(async ct =>
        {
            await work(ct).ConfigureAwait(false);
            return true;
        }, (_, error) => onTick(error));
    }
}

/// <summary>
/// The engine API game code injects; same members as <see cref="IEngineCore"/> (see there for the frame phases and
/// which scheduler to use). Registered as a singleton wrapper over the core engine when
/// <c>altruist:game:engine:diagnostics</c> is <c>false</c> (pass-through) or <c>true</c> (times each task and logs throughput).
/// </summary>
public interface IAltruistEngine : IEngineCore
{
}
