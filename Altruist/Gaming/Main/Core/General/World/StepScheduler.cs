/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Gaming
{
    /// <summary>
    /// Runs the independent units of one engine step (rooms, worlds) on several cores. Each unit
    /// is stepped by exactly one thread per call, so a unit's own simulation stays single-threaded
    /// and deterministic; what is shared between units must be thread-safe.
    /// </summary>
    public interface IStepScheduler
    {
        /// <summary>Threads a call may use (1: everything on the calling thread).</summary>
        int Workers { get; }

        /// <summary>
        /// Calls <paramref name="body"/> once for every item and returns when all are done. Every
        /// item runs even when some throw; the failures are thrown together afterwards
        /// (<see cref="AggregateException"/>).
        /// </summary>
        void ForEach<T>(IReadOnlyList<T> items, Action<T> body);
    }

    /// <summary>
    /// The default <see cref="IStepScheduler"/>. Config <c>altruist:game:engine:step-workers</c>:
    /// <c>1</c> (default, no threads), a number, or <c>auto</c> (one per core).
    /// </summary>
    [Service(typeof(IStepScheduler))]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnMissingService(typeof(IStepScheduler))]
    public sealed class StepScheduler : IStepScheduler
    {
        /// <summary>Everything on the calling thread.</summary>
        public static readonly StepScheduler Inline = new(1);

        private readonly ParallelOptions _options;

        /// <summary>DI constructor: reads <c>altruist:game:engine:step-workers</c> (see <see cref="Parse"/>).</summary>
        [ActivatorUtilitiesConstructor]
        public StepScheduler([AppConfigValue("altruist:game:engine:step-workers", "1")] string? workers)
            : this(Parse(workers))
        {
        }

        /// <summary>A scheduler with <paramref name="workers"/> threads (1 = inline).</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="workers"/> is below 1.</exception>
        public StepScheduler(int workers)
        {
            if (workers < 1)
                throw new ArgumentOutOfRangeException(nameof(workers), workers, "step-workers must be at least 1.");
            Workers = workers;
            _options = new ParallelOptions { MaxDegreeOfParallelism = workers };
        }

        /// <inheritdoc/>
        public int Workers { get; }

        /// <summary><c>auto</c> (or empty) = one per core, otherwise a positive number.</summary>
        public static int Parse(string? workers)
        {
            var w = workers?.Trim();
            if (string.IsNullOrEmpty(w) || w.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return Math.Max(1, Environment.ProcessorCount);
            if (int.TryParse(w, out var n) && n >= 1)
                return n;
            throw new ArgumentException($"altruist:game:engine:step-workers must be 'auto' or a positive number, got '{workers}'.", nameof(workers));
        }

        /// <inheritdoc/>
        public void ForEach<T>(IReadOnlyList<T> items, Action<T> body)
        {
            var count = items.Count;
            if (count == 0)
                return;
            if (Workers == 1 || count == 1)
            {
                List<Exception>? errors = null;
                for (var i = 0; i < count; i++)
                {
                    try
                    { body(items[i]); }
                    catch (Exception ex) { (errors ??= new()).Add(ex); }
                }
                if (errors is not null)
                    throw new AggregateException(errors);
                return;
            }

            ConcurrentQueue<Exception>? failures = null;
            Parallel.For(0, count, _options, i =>
            {
                try
                { body(items[i]); }
                catch (Exception ex)
                {
                    if (failures is null) Interlocked.CompareExchange(ref failures, new ConcurrentQueue<Exception>(), null);
                    failures!.Enqueue(ex);
                }
            });
            if (failures is { IsEmpty: false })
                throw new AggregateException(failures);
        }
    }
}
