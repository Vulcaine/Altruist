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


public class TaskIdentifier : IEquatable<TaskIdentifier>
{
    public string Id { get; }

    public int Seed { get; }

    public TaskIdentifier(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        // Not used for security — just a non-deterministic seed for task scheduling jitter
#pragma warning disable CA5394
        Seed = Random.Shared.Next();
#pragma warning restore CA5394
    }

    public bool Equals(TaskIdentifier? other)
    {
        if (other == null)
            return false;
        return Id.Equals(other.Id, StringComparison.Ordinal);
    }

    public override int GetHashCode() => Id.GetHashCode();

    public static TaskIdentifier FromType(Type type) => new(type.FullName!);

    public static TaskIdentifier FromDelegate(Delegate del)
    {
        var method = del.Method;
        var declaringType = method.DeclaringType?.FullName ?? $"UnknownType_{Guid.NewGuid()}";
        var methodName = method.Name;
        return new TaskIdentifier($"{declaringType}.{methodName}");
    }

    public override string ToString() => Id;
}


public interface IEngineCore
{
    CycleRate Rate { get; }
    bool Enabled { get; }
    void Enable();
    void Disable();
    void RegisterCronJob(Delegate jobDelegate, string cronExpression, object? serviceInstance = null);
    void Start(CancellationToken token);
    void Stop();

    /// <summary>
    /// Runs <paramref name="taskDelegate"/> on the engine loop at <paramref name="cycleRate"/>;
    /// <c>null</c> = every frame.
    /// </summary>
    void ScheduleTask(Delegate taskDelegate, CycleRate? cycleRate = null);
    void SendTask(TaskIdentifier taskId, Delegate taskDelegate);
    void WaitForNextTick(Delegate task);
    void WaitForNextTick(Action task);
    void WaitForNextTick(Func<Task> task);
    void SyncCommit(Action commit);
    Task<T> SyncCommit<T>(Func<T> commit);
    TaskIdentifier ScheduleEffect(
         CycleRate cycleRate,
         DateTime expiresAtUtc,
         Action<float> step);


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

public interface IAltruistEngine : IEngineCore
{
}
