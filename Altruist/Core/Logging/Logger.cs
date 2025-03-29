
using Microsoft.Extensions.Logging;

public class AltruistLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _frameworkVersion;

    public AltruistLogger(string categoryName, string frameworkVersion)
    {
        _categoryName = categoryName;
        var versionParts = frameworkVersion.Split('.');
        _frameworkVersion = $"{versionParts[0]}.{versionParts[1]}";
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var logMessage = formatter(state, exception);

        // Map log level to corresponding color for the ALTRUIST prefix
        var logLevelColor = logLevel switch
        {
            LogLevel.Trace => ConsoleColor.Gray, // For trace (if used)
            LogLevel.Debug => ConsoleColor.Magenta, // Purple for debug
            LogLevel.Information => ConsoleColor.Blue, // Blue for info
            LogLevel.Warning => ConsoleColor.Yellow, // Yellow for warning
            LogLevel.Error => ConsoleColor.Red, // Red for error
            LogLevel.Critical => ConsoleColor.Red, // Red for critical
            LogLevel.None => ConsoleColor.Gray, // None default color (could be gray or others)
            _ => ConsoleColor.White // Fallback to white if not matched
        };

        // Prepare formatted log message with colored ALTRUIST version part
        var versionMessage = $"[ALTRUIST-{_frameworkVersion}]"; // Version part
        var formattedMessage = $"{versionMessage} {logMessage}"; // Full message with version and log message

        // Set console color for the ALTRUIST version part
        Console.ForegroundColor = logLevelColor;

        // Write the ALTRUIST version part to the console
        Console.Write(versionMessage);

        // Reset color and write the actual log message in white
        Console.ResetColor();
        Console.WriteLine($" {logMessage}"); // Log message in white
    }
}


public class AltruistLoggerProvider : ILoggerProvider
{
    private readonly string _frameworkVersion;

    public AltruistLoggerProvider(string frameworkVersion)
    {
        _frameworkVersion = frameworkVersion;
    }

    public ILogger CreateLogger(string categoryName)
    {
        // Create and return a new AltruistLogger
        return new AltruistLogger(categoryName, _frameworkVersion);
    }

    public void Dispose()
    {
        // No resources to dispose of in this example
    }
}
