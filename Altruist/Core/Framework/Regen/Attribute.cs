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

public enum CycleUnit
{
    Hz,
    Seconds,
    Milliseconds,
    Ticks
}

/// <summary>
/// Represents the cycle rate of an engine in different time units, calculating the number of ticks per cycle.
/// </summary>
public class CycleRate
{
    /// <summary>
    /// The computed tick interval at which cycles occur. A lower value means a faster cycle rate.
    /// </summary>
    public long Value { get; }

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
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class CycleAttribute : Attribute
{
    /// <summary>
    /// Gets the cron expression to schedule the method execution, if provided.
    /// </summary>
    public string? Cron { get; }

    /// <summary>
    /// Gets the frequency in Hertz to schedule the method execution, if provided.
    /// </summary>
    public CycleRate? Rate { get; }

    /// <summary>
    /// Gets a value indicating whether the method should be executed in real-time.
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
    /// <param name="cron">The cron expression defining the schedule for the method.</param>
    /// <exception cref="ArgumentNullException">Thrown when the <paramref name="cron"/> is null.</exception>
    public CycleAttribute(string cron)
    {
        Cron = cron ?? throw new ArgumentNullException(nameof(cron));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CycleAttribute"/> class, with a frequency in Hertz.
    /// </summary>
    /// <param name="frequencyHz">The frequency in Hertz (times per second) for scheduling the method.</param>
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
    /// Determines if the method is scheduled based on a frequency in Hertz.
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
