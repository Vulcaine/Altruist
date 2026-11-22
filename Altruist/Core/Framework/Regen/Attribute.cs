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

/// <summary>
/// How the number given to <see cref="CycleRate"/> / <see cref="CycleAttribute"/> is interpreted.
/// </summary>
/// <remarks>
/// Note the default for both is <see cref="Ticks"/> (engine frames), where a HIGHER number means SLOWER
/// execution; for a "times per second" rate pass <see cref="Hz"/> or <see cref="Seconds"/> explicitly.
/// </remarks>
public enum CycleUnit
{
    /// <summary>Cycles per second; must not exceed the engine frame rate (scheduling throws otherwise).</summary>
    Hz,
    /// <summary>Cycles per second, scheduled by wall-clock period (not capped against the engine rate at registration).</summary>
    Seconds,
    /// <summary>Cycles per millisecond (effectively capped by the engine frame rate).</summary>
    Milliseconds,
    /// <summary>Engine frames between runs: 1 = every frame, 2 = every second frame, and so on.</summary>
    Ticks
}

/// <summary>
/// Represents the cycle rate of an engine in different time units, calculating the number of ticks per cycle.
/// </summary>
/// <remarks>
/// Used for the engine's own rate, by <see cref="CycleAttribute"/>, and when scheduling tasks or effects on the
/// engine programmatically. Immutable.
/// </remarks>
/// <example>
/// <code>
/// var everyFrame   = new CycleRate(1);                    // Ticks: every engine frame
/// var everyThird   = new CycleRate(3, CycleUnit.Ticks);   // every 3rd frame
/// var tenPerSecond = new CycleRate(10, CycleUnit.Hz);     // 10 times per second
/// </code>
/// </example>
public class CycleRate
{
    /// <summary>
    /// The computed interval value; its meaning depends on <see cref="Unit"/>: frames between runs for
    /// <see cref="CycleUnit.Ticks"/>, cycles per second for <see cref="CycleUnit.Hz"/>, and the cycle period in
    /// <see cref="TimeSpan"/> ticks (100 ns) for <see cref="CycleUnit.Seconds"/> / <see cref="CycleUnit.Milliseconds"/>.
    /// Prefer <see cref="Frequency"/> + <see cref="Unit"/> when you need the user-facing rate.
    /// </summary>
    public long Value { get; }

    /// <summary>Unit the rate was created with.</summary>
    public CycleUnit Unit { get; }

    /// <summary>The frequency this rate was created with (cycles per <see cref="Unit"/>, or the frame interval for Ticks).</summary>
    public int Frequency { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CycleRate"/> class.
    /// </summary>
    /// <param name="frequencyHz">The desired frequency in Hertz (Hz).</param>
    /// <param name="unit">The time unit for frequency interpretation. Default is Ticks.</param>
    /// <exception cref="ArgumentException">Thrown if the provided frequency is non-positive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if an unsupported <see cref="CycleUnit"/> is provided.</exception>
    /// <remarks>
    /// The frequency represents how often a cycle occurs per the given unit:
    ///
    /// - **Hz / Seconds:** A frequency of 30 means 30 cycles per second. For Seconds and
    ///   Milliseconds <see cref="Value"/> holds the cycle period in <see cref="TimeSpan"/> ticks (100 ns).
    /// - **Milliseconds:** A frequency of 30 means 30 cycles per millisecond (capped by the engine frame rate).
    /// - **Ticks:** The value is a number of engine frames: <c>[Cycle(2)]</c> runs every 2nd frame,
    ///   meaning **a higher value results in slower execution**.
    /// </remarks>
    public CycleRate(int frequencyHz, CycleUnit unit = CycleUnit.Ticks)
    {
        if (frequencyHz <= 0)
            throw new ArgumentException("Frequency must be a positive value.", nameof(frequencyHz));

        Unit = unit;
        Frequency = frequencyHz;

        Value = unit switch
        {
            // 30 Hz → 30 cycles per second → execute every (10,000,000 / 30) ticks
            CycleUnit.Seconds => TimeSpan.TicksPerSecond / frequencyHz,

            // 30 Hz → 30 cycles per millisecond → execute every (10,000 / 30) ticks
            CycleUnit.Milliseconds => (TimeSpan.TicksPerSecond / 1000) / frequencyHz,

            // 30 Hz → Direct mapping to ticks → Higher Hz means fewer cycles per tick (slower)
            CycleUnit.Ticks => frequencyHz,

            CycleUnit.Hz => frequencyHz,

            _ => throw new ArgumentOutOfRangeException(nameof(unit), "Invalid cycle unit.")
        };
    }
}



/// <summary>
/// Represents an attribute that marks methods for scheduling.
/// The methods can be scheduled to execute at specific frequencies or using cron expressions.
/// </summary>
/// <remarks>
/// This attribute is used to define the scheduling behavior for methods in the system, allowing
/// them to be executed at specified intervals or times. It supports cron-based scheduling, frequency-based
/// scheduling (in Hertz), or real-time execution by default.
/// <para>
/// Discovery: at startup the engine's method scheduler scans every service registered in the root DI container
/// (e.g. via <c>[Service]</c>) for <c>[Cycle]</c> methods (public or non-public, instance) and runs them on that
/// same instance. The method must be parameterless and return <see cref="Task"/> or <c>void</c>; other shapes
/// throw at startup. Requires the game engine to be enabled; methods on classes not registered in DI are never found.
/// </para>
/// <para>
/// Modes (first match wins): cron (<see cref="Cron"/>, 5-field cron evaluated in UTC on a timer, independent of
/// the frame loop), configured rate (<see cref="Config"/>), fixed rate (<see cref="Rate"/>), else every frame.
/// An async frame-scheduled method is not overlapped with itself while a previous run is still in flight.
/// </para>
/// <para>
/// Use <c>[Cycle]</c> for fixed recurring work declared on a service. For work started/stopped at runtime or
/// with an expiry, schedule it on the engine programmatically instead.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Service]
/// public class Housekeeping
/// {
///     [Cycle]                              // every engine frame
///     public void Tick() { }
///
///     [Cycle(5, CycleUnit.Hz)]             // 5 times per second
///     public Task SyncAsync() =&gt; Task.CompletedTask;
///
///     [Cycle(10)]                          // every 10th frame (default unit is Ticks)
///     public void Sweep() { }
///
///     [Cycle("0 * * * *")]                 // top of every hour (UTC)
///     public Task HourlyAsync() =&gt; Task.CompletedTask;
///
///     [Cycle(Config = "myapp:sync:hz", Default = 2, Unit = CycleUnit.Hz)]
///     public void ConfiguredSync() { }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class CycleAttribute : Attribute
{
    /// <summary>
    /// Gets the cron expression to schedule the method execution, if provided.
    /// </summary>
    public string? Cron { get; }

    /// <summary>
    /// Gets the fixed rate set by the <c>(int, CycleUnit)</c> constructor, or null. Despite the historical wording,
    /// the default unit is <see cref="CycleUnit.Ticks"/> (frames between runs), not Hertz.
    /// </summary>
    public CycleRate? Rate { get; }

    /// <summary>
    /// Gets a value indicating whether the method should be executed in real-time.
    /// True only for the parameterless constructor (every frame, unless <see cref="Config"/> is set).
    /// </summary>
    public bool Realtime { get; }

    /// <summary>
    /// Configuration key holding the rate (e.g. <c>"mygame:matchmaking:tick-hz"</c>), read when the
    /// method is registered. Combine with <see cref="Default"/> and <see cref="Unit"/>:
    /// <c>[Cycle(Config = "mygame:matchmaking:tick-hz", Default = 10, Unit = CycleUnit.Hz)]</c>.
    /// A missing or non-positive value uses <see cref="Default"/>; without a default the method runs every frame.
    /// </summary>
    public string? Config { get; set; }

    /// <summary>Rate used when <see cref="Config"/> is missing or not positive (0 = every frame).</summary>
    public int Default { get; set; }

    /// <summary>Unit of the configured rate (<see cref="Config"/>); frames (<see cref="CycleUnit.Ticks"/>) by default.</summary>
    public CycleUnit Unit { get; set; } = CycleUnit.Ticks;

    /// <summary>
    /// Marks the method for real-time execution: it runs every engine frame
    /// (or at the configured rate when <see cref="Config"/> is set).
    /// </summary>
    public CycleAttribute()
    {
        Realtime = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CycleAttribute"/> class, with a cron expression for scheduling.
    /// </summary>
    /// <param name="cron">The cron expression defining the schedule for the method (standard 5-field
    /// <c>minute hour day month weekday</c>, evaluated in UTC; parsed at startup and invalid expressions throw).</param>
    /// <exception cref="ArgumentNullException">Thrown when the <paramref name="cron"/> is null.</exception>
    public CycleAttribute(string cron)
    {
        Cron = cron ?? throw new ArgumentNullException(nameof(cron));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CycleAttribute"/> class with a fixed rate.
    /// </summary>
    /// <remarks>
    /// With the default <see cref="CycleUnit.Ticks"/>, <paramref name="frequencyHz"/> is the number of frames between
    /// runs (<c>[Cycle(2)]</c> = every 2nd frame). Pass <see cref="CycleUnit.Hz"/> for times per second.
    /// </remarks>
    /// <param name="frequencyHz">The rate value, interpreted according to <paramref name="unit"/> (must be positive).</param>
    /// <param name="unit">How to interpret <paramref name="frequencyHz"/>; defaults to <see cref="CycleUnit.Ticks"/> (frames).</param>
    /// <exception cref="ArgumentException">Thrown when the <paramref name="frequencyHz"/> is less than or equal to 0.</exception>
    public CycleAttribute(int frequencyHz, CycleUnit unit = CycleUnit.Ticks)
    {
        if (frequencyHz <= 0)
            throw new ArgumentException("Frequency must be a positive value.", nameof(frequencyHz));
        Rate = new CycleRate(frequencyHz, unit);
    }


    /// <summary>
    /// Determines if the method is scheduled using a cron expression.
    /// </summary>
    /// <returns>True if a cron expression is set; otherwise, false.</returns>
    public bool IsCron() => !string.IsNullOrEmpty(Cron);

    /// <summary>
    /// Determines if the method is scheduled at a fixed rate (<see cref="Rate"/> is set).
    /// </summary>
    /// <returns>True if the frequency is set; otherwise, false.</returns>
    public bool IsFrequency() => Rate != null;

    /// <summary>
    /// Determines if the method is set to execute in real-time.
    /// </summary>
    /// <returns>True if the method is to execute in real-time; otherwise, false.</returns>
    public bool IsRealTime() => Realtime;

    /// <summary>True when the rate comes from configuration (<see cref="Config"/>).</summary>
    public bool IsConfigured() => !IsCron() && !string.IsNullOrWhiteSpace(Config);

    /// <summary>Human-readable schedule description used in the startup log (e.g. "every 2 frame(s)", "5Hz", readable cron).</summary>
    public override string ToString()
    {
        if (IsCron())
            return "🕒 " + CronMapper.MapCronToReadableFormat(Cron!);

        if (IsFrequency())
            return Rate!.Unit switch
            {
                CycleUnit.Ticks => "⚡ every " + Rate.Frequency + " frame(s)",
                CycleUnit.Milliseconds => "⚡ " + Rate.Frequency + "/ms",
                _ => "⚡ " + Rate.Frequency + "Hz",
            };

        if (IsConfigured())
            return $"⚙ {Config} (default {(Default > 0 ? Default + " " + Unit : "every frame")})";

        return "⚡ every frame";
    }
}
