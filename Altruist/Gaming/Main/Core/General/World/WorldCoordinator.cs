/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Gaming
{
    /// <summary>
    /// The engine's world step: called once per engine frame (<c>world-step: inline</c>) or per
    /// world-worker pass (<c>world-step: worker</c>) with the elapsed real time.
    /// The default implementation is <see cref="WorldCoordinator"/>; registering your own
    /// <c>[Service(typeof(IGameWorldOrganizer))]</c> replaces it.
    /// </summary>
    public interface IGameWorldOrganizer
    {
        void Step(float deltaTime);
    }

    /// <summary>How the <see cref="WorldCoordinator"/> drives a stepper.</summary>
    public enum StepMode
    {
        /// <summary><see cref="IWorldStepper.Step"/> once per frame with the real frame time.</summary>
        Variable,

        /// <summary><see cref="IWorldStepper.FixedStep"/> 0..N times per frame with a constant dt (<see cref="IWorldStepper.FixedHz"/>).</summary>
        Fixed,
    }

    /// <summary>One fixed step.</summary>
    /// <param name="Step">The stepper's own step counter (1 for its first step).</param>
    /// <param name="Dt">Step length in seconds; the same for every step (<c>1f / FixedHz</c>).</param>
    /// <param name="IndexInFrame">0-based index of this step within the frame.</param>
    /// <param name="StepsInFrame">Steps this frame runs for the stepper.</param>
    public readonly record struct FixedStep(long Step, float Dt, int IndexInFrame, int StepsInFrame);

    /// <summary>One coordinator frame, as a stepper sees it.</summary>
    /// <param name="Frame">Coordinator frame counter (1 for the first frame).</param>
    /// <param name="RealDt">Real time since the previous frame (seconds).</param>
    /// <param name="Dt">
    /// The time this frame represents: for fixed steppers <see cref="RealDt"/> clamped to
    /// <c>max-frame-delta</c>; for variable steppers equal to <see cref="RealDt"/>.
    /// </param>
    /// <param name="FixedSteps">Fixed steps this frame runs for the stepper (0 for variable steppers).</param>
    /// <param name="Overrun">The frame hit <c>max-steps-per-frame</c>.</param>
    public readonly record struct FrameInfo(long Frame, double RealDt, double Dt, int FixedSteps, bool Overrun);

    /// <summary>
    /// Something the engine advances every frame: a world, a set of match rooms, a force runtime.
    /// Register it with <c>[Service(typeof(IWorldStepper))]</c>; the <see cref="WorldCoordinator"/>
    /// steps every registered stepper once per frame, in registration order:
    /// <c>BeforeSteps</c>, then <c>Step</c> (variable) or <c>FixedStep</c> × N (fixed), then <c>AfterSteps</c>.
    /// </summary>
    public interface IWorldStepper
    {
        StepMode Mode => StepMode.Variable;

        /// <summary>Fixed mode: steps per second. Ignored in variable mode.</summary>
        int FixedHz => 0;

        /// <summary>Once per frame, before the steps; <paramref name="frame"/> already tells how many fixed steps follow.</summary>
        void BeforeSteps(in FrameInfo frame) { }

        /// <summary>Variable mode: once per frame with the real frame time.</summary>
        void Step(float dt) { }

        /// <summary>Fixed mode: once per fixed step (0..max-steps-per-frame times per frame).</summary>
        void FixedStep(in FixedStep step) { }

        /// <summary>Once per frame, after the steps (timers, bookkeeping).</summary>
        void AfterSteps(in FrameInfo frame) { }
    }

    /// <summary>
    /// The default <see cref="IGameWorldOrganizer"/>: a composite that steps every registered
    /// <see cref="IWorldStepper"/> — the 2D/3D world organizers with the variable frame time and
    /// fixed-rate steppers (rooms, force runtimes) with a constant dt from one
    /// <see cref="FixedStepClock"/> per stepper. Config (<c>altruist:game:engine:fixed-step</c>):
    /// <c>max-frame-delta</c> (0.25 s), <c>max-steps-per-frame</c> (8), <c>overrun</c> (drop | carry | slow-motion).
    /// A stepper that throws is logged and does not stop the others.
    /// </summary>
    [Service(typeof(IGameWorldOrganizer))]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnMissingService(typeof(IGameWorldOrganizer))]
    public sealed class WorldCoordinator : IGameWorldOrganizer
    {
        /// <summary>
        /// The engine's meter: <c>altruist.engine.frame.duration</c> (ms per frame),
        /// <c>altruist.engine.stepper.duration</c> (ms per stepper and frame, tagged <c>stepper</c>),
        /// <c>altruist.engine.fixed_steps</c> and <c>altruist.engine.overruns</c> (frames that hit
        /// <c>max-steps-per-frame</c>, tagged <c>stepper</c>).
        /// </summary>
        public const string MeterName = "Altruist.Engine";

        private static readonly Meter EngineMeter = new(MeterName);
        private static readonly Histogram<double> FrameDuration =
            EngineMeter.CreateHistogram<double>("altruist.engine.frame.duration", "ms", "Wall time of one coordinator frame.");
        private static readonly Histogram<double> StepperDuration =
            EngineMeter.CreateHistogram<double>("altruist.engine.stepper.duration", "ms", "Wall time of one stepper in one frame.");
        private static readonly Counter<long> FixedSteps =
            EngineMeter.CreateCounter<long>("altruist.engine.fixed_steps", description: "Fixed steps run.");
        private static readonly Counter<long> Overruns =
            EngineMeter.CreateCounter<long>("altruist.engine.overruns", description: "Frames that hit max-steps-per-frame.");

        private readonly Lazy<IEnumerable<IWorldStepper>> _source;
        private readonly double _maxFrameDelta;
        private readonly int _maxSteps;
        private readonly OverrunPolicy _overrun;
        private readonly ILogger _logger;
        private Entry[]? _entries;
        private long _frame;

        private sealed class Entry
        {
            public required IWorldStepper Stepper;
            public required KeyValuePair<string, object?> Tag;
            public FixedStepClock? Clock;
            public long LastFaultLog;
            public long SuppressedFaults;
        }

        /// <summary>
        /// DI constructor. Steppers are resolved on the first step, so a stepper may depend on the
        /// engine (which depends on this coordinator).
        /// </summary>
        [ActivatorUtilitiesConstructor]
        public WorldCoordinator(
            Lazy<IEnumerable<IWorldStepper>> steppers,
            [AppConfigValue("altruist:game:engine:fixed-step:max-frame-delta", "0.25")] double maxFrameDelta,
            [AppConfigValue("altruist:game:engine:fixed-step:max-steps-per-frame", "8")] int maxStepsPerFrame,
            [AppConfigValue("altruist:game:engine:fixed-step:overrun", "drop")] string? overrun,
            ILoggerFactory? loggerFactory = null)
            : this(steppers, maxFrameDelta, maxStepsPerFrame, FixedStepClock.ParseOverrun(overrun), loggerFactory)
        {
        }

        /// <summary>Manual constructor (tests, custom hosts).</summary>
        public WorldCoordinator(
            IEnumerable<IWorldStepper> steppers,
            double maxFrameDelta = 0.25,
            int maxStepsPerFrame = 8,
            OverrunPolicy overrun = OverrunPolicy.Drop,
            ILoggerFactory? loggerFactory = null)
            : this(new Lazy<IEnumerable<IWorldStepper>>(() => steppers), maxFrameDelta, maxStepsPerFrame, overrun, loggerFactory)
        {
        }

        private WorldCoordinator(
            Lazy<IEnumerable<IWorldStepper>> steppers,
            double maxFrameDelta,
            int maxStepsPerFrame,
            OverrunPolicy overrun,
            ILoggerFactory? loggerFactory)
        {
            if (maxFrameDelta <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFrameDelta), maxFrameDelta, "max-frame-delta must be positive.");
            if (maxStepsPerFrame <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame), maxStepsPerFrame, "max-steps-per-frame must be positive.");
            _source = steppers;
            _maxFrameDelta = maxFrameDelta;
            _maxSteps = maxStepsPerFrame;
            _overrun = overrun;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WorldCoordinator>();
        }

        public double MaxFrameDelta => _maxFrameDelta;
        public int MaxStepsPerFrame => _maxSteps;
        public OverrunPolicy Overrun => _overrun;

        /// <summary>Frames stepped so far.</summary>
        public long Frame => _frame;

        /// <summary>The steppers, in the order they are stepped.</summary>
        public IReadOnlyList<IWorldStepper> Steppers => Entries().Select(e => e.Stepper).ToList();

        /// <summary>The clock of a fixed stepper (null for variable steppers or unknown ones).</summary>
        public FixedStepClock? ClockOf(IWorldStepper stepper) =>
            Entries().FirstOrDefault(e => ReferenceEquals(e.Stepper, stepper))?.Clock;

        public void Step(float deltaTime)
        {
            var entries = Entries();
            _frame++;
            double realDt = deltaTime;
            var measure = FrameDuration.Enabled || StepperDuration.Enabled;
            var frameStart = measure ? Stopwatch.GetTimestamp() : 0;

            foreach (var e in entries)
            {
                var stepper = e.Stepper;
                var stepperStart = measure ? Stopwatch.GetTimestamp() : 0;
                if (e.Clock is not { } clock)
                {
                    var frame = new FrameInfo(_frame, realDt, realDt, 0, false);
                    Guard(e, "BeforeSteps", stepper, frame, static (s, f) => s.BeforeSteps(in f));
                    try
                    { stepper.Step(deltaTime); }
                    catch (Exception ex) { ReportFault(e, "Step", ex); }
                    Guard(e, "AfterSteps", stepper, frame, static (s, f) => s.AfterSteps(in f));
                }
                else
                {
                    var steps = clock.Advance(realDt, out var clamped, out var overrun);
                    var info = new FrameInfo(_frame, realDt, clamped, steps, overrun);
                    Guard(e, "BeforeSteps", stepper, info, static (s, f) => s.BeforeSteps(in f));
                    var first = clock.TotalSteps - steps;
                    for (var i = 0; i < steps; i++)
                    {
                        try
                        { stepper.FixedStep(new FixedStep(first + i + 1, clock.Dt, i, steps)); }
                        catch (Exception ex) { ReportFault(e, "FixedStep", ex); }
                    }
                    Guard(e, "AfterSteps", stepper, info, static (s, f) => s.AfterSteps(in f));
                    if (steps > 0) FixedSteps.Add(steps, e.Tag);
                    if (overrun) Overruns.Add(1, e.Tag);
                }
                if (measure)
                    StepperDuration.Record(Stopwatch.GetElapsedTime(stepperStart).TotalMilliseconds, e.Tag);
            }
            if (measure)
                FrameDuration.Record(Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds);
        }

        private void Guard(Entry e, string phase, IWorldStepper stepper, FrameInfo frame, Action<IWorldStepper, FrameInfo> call)
        {
            try
            { call(stepper, frame); }
            catch (Exception ex) { ReportFault(e, phase, ex); }
        }

        private Entry[] Entries()
        {
            if (_entries is { } built)
                return built;

            var list = new List<Entry>();
            var seen = new HashSet<IWorldStepper>(ReferenceEqualityComparer.Instance);
            foreach (var stepper in _source.Value ?? Array.Empty<IWorldStepper>())
            {
                // One instance registered under several service types is stepped once.
                if (stepper is null || !seen.Add(stepper))
                    continue;
                FixedStepClock? clock = null;
                if (stepper.Mode == StepMode.Fixed)
                {
                    if (stepper.FixedHz <= 0)
                        throw new InvalidOperationException(
                            $"{stepper.GetType().FullName} is a fixed-mode IWorldStepper with FixedHz {stepper.FixedHz}; it must be positive.");
                    clock = new FixedStepClock(stepper.FixedHz, _maxFrameDelta, _maxSteps, _overrun);
                }
                list.Add(new Entry { Stepper = stepper, Clock = clock, Tag = new("stepper", stepper.GetType().Name) });
            }
            _entries = list.ToArray();
            return _entries;
        }

        private void ReportFault(Entry e, string phase, Exception ex)
        {
            var now = Stopwatch.GetTimestamp();
            if (e.LastFaultLog != 0 && now - e.LastFaultLog < Stopwatch.Frequency * 10)
            {
                e.SuppressedFaults++;
                return;
            }
            var suppressed = e.SuppressedFaults;
            e.SuppressedFaults = 0;
            e.LastFaultLog = now;
            _logger.LogError(ex, "World stepper {Stepper}.{Phase} threw ({Suppressed} more since the last report); the other steppers keep running.",
                e.Stepper.GetType().Name, phase, suppressed);
        }
    }
}
