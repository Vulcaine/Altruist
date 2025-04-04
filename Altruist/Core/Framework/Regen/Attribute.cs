
public enum CycleUnit
{
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
    /// - **Seconds:** A frequency of 30 Hz means 30 cycles per second (faster execution).
    /// - **Milliseconds:** A frequency of 30 Hz means 30 cycles per millisecond (extremely fast execution).
    /// - **Ticks:** The frequency directly represents the number of cycles per tick, meaning **higher Hz results in slower execution**.
    /// 
    /// A **higher frequency (Hz) results in faster execution** for time-based units (Seconds, Milliseconds).
    /// However, for **Ticks, a higher frequency means slower execution** since it directly maps to CPU tick rate.
    /// </remarks>
    public CycleRate(int frequencyHz, CycleUnit? unit = CycleUnit.Ticks)
    {
        if (frequencyHz <= 0)
            throw new ArgumentException("Frequency must be a positive value.", nameof(frequencyHz));

        Value = unit switch
        {
            // 30 Hz → 30 cycles per second → execute every (10,000,000 / 30) ticks
            CycleUnit.Seconds => TimeSpan.TicksPerSecond / frequencyHz,

            // 30 Hz → 30 cycles per millisecond → execute every (10,000 / 30) ticks
            CycleUnit.Milliseconds => (TimeSpan.TicksPerSecond / 1000) / frequencyHz,

            // 30 Hz → Direct mapping to ticks → Higher Hz means fewer cycles per tick (slower)
            CycleUnit.Ticks => frequencyHz,

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
    /// Initializes a new instance of the <see cref="CycleAttribute"/> class, marking the method for real-time execution, which means it will match the Engine's update frequency.
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
    public CycleAttribute(int frequencyHz, CycleUnit? unit = CycleUnit.Ticks)
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

    public override string ToString()
    {
        if (IsCron())
            return "🕒 " + CronMapper.MapCronToReadableFormat(Cron!);

        if (IsFrequency())
            return "⚡ " + Rate!.Value.ToString() + "Hz";

        return "⚡ Realtime";
    }
}
