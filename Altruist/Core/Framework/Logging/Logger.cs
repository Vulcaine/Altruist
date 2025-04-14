
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
        var logLevelColor = logLevel switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Magenta,
            LogLevel.Information => ConsoleColor.Blue,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.Red,
            LogLevel.None => ConsoleColor.Gray,
            _ => ConsoleColor.White
        };

        var versionMessage = $"[ALTRUIST-{_frameworkVersion}]";
        var formattedMessage = $"{versionMessage} {logMessage}";
        Console.ForegroundColor = logLevelColor;

        Console.Write(versionMessage);
        Console.ResetColor();
        Console.WriteLine($" {logMessage}");
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
