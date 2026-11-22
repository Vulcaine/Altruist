using Microsoft.Extensions.Logging;

namespace Tests.Gaming;

/// <summary>Logger factory whose loggers record every error-level entry's exception.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
{
    private readonly List<Exception?> _errors = new();

    public IReadOnlyList<Exception?> Errors
    {
        get { lock (_errors) return _errors.ToList(); }
    }

    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error)
            lock (_errors) _errors.Add(exception);
    }
}
