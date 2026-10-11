/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Diagnostics;
using System.Diagnostics.Metrics;

using Altruist.Gaming;

namespace Altruist.Dashboard
{
    /// <summary>Timing of one world stepper (or the whole coordinator frame) over the last seconds.</summary>
    /// <param name="Name">Stepper type name, or <c>frame</c>.</param>
    /// <param name="Samples">Measurements in the window.</param>
    /// <param name="AverageMs">Mean wall time.</param>
    /// <param name="P95Ms">95th percentile wall time.</param>
    /// <param name="MaxMs">Longest wall time.</param>
    /// <param name="FixedStepsPerSecond">Fixed steps run per second (fixed-step steppers).</param>
    /// <param name="Overruns">Frames in the window that hit max-steps-per-frame.</param>
    public sealed record StepperTimingDto(string Name, int Samples, double AverageMs, double P95Ms, double MaxMs, double FixedStepsPerSecond, long Overruns);

    /// <summary>
    /// Listens to the engine's <c>Altruist.Engine</c> meter (<see cref="WorldCoordinator"/>) and keeps the last
    /// <see cref="Window"/> of frame and stepper timings, fixed steps and overruns for the dashboard. Listening costs a
    /// lock and an array write per measurement; it starts with the first dashboard read and stops after a minute
    /// without one.
    /// </summary>
    [Service]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardEngineMetrics : IDisposable
    {
        /// <summary>How far back the figures look.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);

        private readonly object _gate = new();
        private readonly Dictionary<string, Series> _durations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Series> _fixedSteps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Series> _overruns = new(StringComparer.Ordinal);
        private MeterListener? _listener;
        private long _listeningSince;
        private long _lastRead;

        private sealed class Series
        {
            private readonly Queue<(long At, double Value)> _items = new();

            public void Add(long at, double value)
            {
                _items.Enqueue((at, value));
                while (_items.Count > 0 && Stopwatch.GetElapsedTime(_items.Peek().At, at) > Window)
                    _items.Dequeue();
            }

            public double[] Values(long now) => _items.Where(i => Stopwatch.GetElapsedTime(i.At, now) <= Window).Select(i => i.Value).ToArray();
        }

        /// <summary>The coordinator frame and each stepper, slowest first; starts listening when it was not.</summary>
        public IReadOnlyList<StepperTimingDto> Read()
        {
            Interlocked.Exchange(ref _lastRead, Stopwatch.GetTimestamp());
            EnsureListening();
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                // Rates over the time actually covered: the window, or less right after listening began.
                var covered = Math.Max(0.5, Math.Min(Window.TotalSeconds, Stopwatch.GetElapsedTime(_listeningSince, now).TotalSeconds));
                return _durations
                    .Select(kv =>
                    {
                        var values = kv.Value.Values(now);
                        var sorted = values.Order().ToArray();
                        var steps = _fixedSteps.TryGetValue(kv.Key, out var f) ? f.Values(now).Sum() : 0;
                        var overruns = _overruns.TryGetValue(kv.Key, out var o) ? (long)o.Values(now).Sum() : 0;
                        return new StepperTimingDto(
                            kv.Key,
                            values.Length,
                            values.Length > 0 ? values.Average() : 0,
                            sorted.Length > 0 ? sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))] : 0,
                            sorted.Length > 0 ? sorted[^1] : 0,
                            steps / covered,
                            overruns);
                    })
                    .OrderByDescending(s => s.Name == "frame")
                    .ThenByDescending(s => s.AverageMs)
                    .ToList();
            }
        }

        private void EnsureListening()
        {
            lock (_gate)
            {
                if (_listener is not null)
                    return;
                _listener = new MeterListener
                {
                    InstrumentPublished = (instrument, listener) =>
                    {
                        if (instrument.Meter.Name == WorldCoordinator.MeterName)
                            listener.EnableMeasurementEvents(instrument);
                    },
                };
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
                _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
                _listeningSince = Stopwatch.GetTimestamp();
                _listener.Start();
            }
        }

        private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRead), now) > Idle)
            {
                StopListening();
                return;
            }
            var name = "frame";
            foreach (var tag in tags)
            {
                if (tag.Key == "stepper")
                    name = tag.Value as string ?? name;
            }
            var target = instrument switch
            {
                "altruist.engine.frame.duration" or "altruist.engine.stepper.duration" => _durations,
                "altruist.engine.fixed_steps" => _fixedSteps,
                "altruist.engine.overruns" => _overruns,
                _ => null,
            };
            if (target is null)
                return;
            lock (_gate)
            {
                if (!target.TryGetValue(name, out var series))
                    target[name] = series = new Series();
                series.Add(now, value);
            }
        }

        private void StopListening()
        {
            MeterListener? listener;
            lock (_gate)
            {
                listener = _listener;
                _listener = null;
                _durations.Clear();
                _fixedSteps.Clear();
                _overruns.Clear();
            }
            // Disposing from inside a measurement callback is allowed; it only stops later events.
            listener?.Dispose();
        }

        /// <inheritdoc/>
        public void Dispose() => StopListening();
    }
}
